using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using UnityEngine;
using UnityEngine.SceneManagement;
using BS_Utils.Gameplay;
using IPA;
using IPALogger = IPA.Logging.Logger;

// Interesting props and methods:
// protected const int ScoreController.kMaxCutScore // 110
// public BeatmapObjectSpawnController.noteWasCutEvent<BeatmapObjectSpawnController, NoteController, NoteCutInfo> // Listened to by scoreManager for its cut event and therefore is raised before combo, multiplier and score changes
// public BeatmapObjectSpawnController.noteWasMissedEvent<BeatmapObjectSpawnController, NoteController> // Same as above, but for misses
// public BeatmapObjectSpawnController.obstacleDidPassAvoidedMarkEvent<BeatmapObjectSpawnController, ObstacleController>
// public int ScoreController.prevFrameScore
// protected ScoreController._baseScore

namespace BeatSaberHTTPStatus {
	[Plugin(RuntimeOptions.SingleStartInit)]
	internal class Plugin {
		public static Plugin instance {get; private set;}

		private StatusManager statusManager = new StatusManager();
		private HTTPServer server;

		private bool headInObstacle = false;

		private GameplayCoreSceneSetupData gameplayCoreSceneSetupData;
		private GamePause gamePause;
		private ScoreController scoreController;
		private StandardLevelGameplayManager standardLevelGameplayManager;
		private MissionLevelGameplayManager missionLevelGameplayManager;
		private MonoBehaviour gameplayManager;
		private GameplayModifiersModelSO gameplayModifiersSO;
		private GameplayModifiers gameplayModifiers;
		private AudioTimeSyncController audioTimeSyncController;
		private BeatmapObjectCallbackController beatmapObjectCallbackController;
		private PlayerHeadAndObstacleInteraction playerHeadAndObstacleInteraction;
		private GameEnergyCounter gameEnergyCounter;
		private Dictionary<NoteCutInfo, NoteData> noteCutMapping = new Dictionary<NoteCutInfo, NoteData>();
		
		/// protected NoteCutInfo CutScoreBuffer._noteCutInfo
		private FieldInfo noteCutInfoField = typeof(CutScoreBuffer).GetField("_noteCutInfo", BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
		/// protected List<CutScoreBuffer> ScoreController._cutScoreBuffers // contains a list of after cut buffers
		private FieldInfo afterCutScoreBuffersField = typeof(ScoreController).GetField("_cutScoreBuffers", BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
		/// private int CutScoreBuffer#_multiplier
		private FieldInfo cutScoreBufferMultiplierField = typeof(CutScoreBuffer).GetField("_multiplier", BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
		/// private static LevelCompletionResults.Rank LevelCompletionResults.GetRankForScore(int score, int maxPossibleScore)
		private MethodInfo getRankForScoreMethod = typeof(LevelCompletionResults).GetMethod("GetRankForScore", BindingFlags.NonPublic | BindingFlags.Static);

		public static readonly string PluginVersion = "$SEMVER_VERSION$"; // Populated by MSBuild
		public static readonly string GameVersion = "$BS_VERSION$"; // Populated by MSBuild

		public string Name {
			get {return "HTTP Status";}
		}

		public string Version {
			get {return PluginVersion;}
		}

		public static IPALogger log;

		[Init]
		public void Init(IPALogger logger) {
			log = logger;
		}

		[OnStart]
		public void OnApplicationStart() {
			if (instance != null) return;
			instance = this;

			PluginTickerScript.TouchInstance();

			server = new HTTPServer(statusManager);
			server.InitServer();
			
			SceneManager.activeSceneChanged += OnActiveSceneChanged;
		}

		[OnExit]
		public void OnApplicationQuit() {
			SceneManager.activeSceneChanged -= OnActiveSceneChanged;

			if (gamePause != null) {
				gamePause.didPauseEvent -= OnGamePause;
				gamePause.didResumeEvent -= OnGameResume;
			}

			if (scoreController != null) {
				scoreController.noteWasCutEvent -= OnNoteWasCut;
				scoreController.noteWasMissedEvent -= OnNoteWasMissed;
				scoreController.scoreDidChangeEvent -= OnScoreDidChange;
				scoreController.comboDidChangeEvent -= OnComboDidChange;
				scoreController.multiplierDidChangeEvent -= OnMultiplierDidChange;
			}

			if (standardLevelGameplayManager != null) {
				standardLevelGameplayManager.levelFinishedEvent -= OnLevelFinished;
				standardLevelGameplayManager.levelFailedEvent -= OnLevelFailed;
			}

			if (missionLevelGameplayManager != null) {
				missionLevelGameplayManager.levelFinishedEvent -= OnLevelFinished;
				missionLevelGameplayManager.levelFailedEvent -= OnLevelFailed;
			}

			if (beatmapObjectCallbackController != null) {
				beatmapObjectCallbackController.beatmapEventDidTriggerEvent -= OnBeatmapEventDidTrigger;
			}

			server.StopServer();
		}

		public async void OnActiveSceneChanged(Scene oldScene, Scene newScene) {
			GameStatus gameStatus = statusManager.gameStatus;

			gameStatus.scene = newScene.name;

			if (newScene.name == "MenuCore") {
				// Menu
				gameStatus.scene = "Menu";

				Gamemode.Init();

				// TODO: get the current song, mode and mods while in menu
				gameStatus.ResetMapInfo();

				gameStatus.ResetPerformance();

				// Release references for AfterCutScoreBuffers that don't resolve due to player leaving the map before finishing.
				noteCutMapping.Clear();

				statusManager.EmitStatusUpdate(ChangedProperties.AllButNoteCut, "menu");
			} else if (newScene.name == "GameCore") {
				// In game
				gameStatus.scene = "Song";

				gamePause = FindFirstOrDefault<GamePause>();
				scoreController = FindFirstOrDefault<ScoreController>();
				gameplayManager = Resources.FindObjectsOfTypeAll<StandardLevelGameplayManager>().FirstOrDefault() as MonoBehaviour ?? Resources.FindObjectsOfTypeAll<MissionLevelGameplayManager>().FirstOrDefault();
				beatmapObjectCallbackController = FindFirstOrDefault<BeatmapObjectCallbackController>();
				gameplayModifiersSO = FindFirstOrDefault<GameplayModifiersModelSO>();
				audioTimeSyncController = FindFirstOrDefault<AudioTimeSyncController>();
				playerHeadAndObstacleInteraction = FindFirstOrDefault<PlayerHeadAndObstacleInteraction>();
				gameEnergyCounter = FindFirstOrDefault<GameEnergyCounter>();

				if (gameplayManager.GetType() == typeof(StandardLevelGameplayManager)) {
					Plugin.log.Info("Standard Level loaded");
					standardLevelGameplayManager = FindFirstOrDefault<StandardLevelGameplayManager>();
					// public event Action StandardLevelGameplayManager#levelFailedEvent;
					standardLevelGameplayManager.levelFailedEvent += OnLevelFailed;
					// public event Action StandardLevelGameplayManager#levelFinishedEvent;
					standardLevelGameplayManager.levelFinishedEvent += OnLevelFinished;
				} else if (gameplayManager.GetType() == typeof(MissionLevelGameplayManager)) {
					Plugin.log.Info("Mission Level loaded");
					missionLevelGameplayManager = FindFirstOrDefault<MissionLevelGameplayManager>();
					// public event Action StandardLevelGameplayManager#levelFailedEvent;
					missionLevelGameplayManager.levelFailedEvent += OnLevelFailed;
					// public event Action StandardLevelGameplayManager#levelFinishedEvent;
					missionLevelGameplayManager.levelFinishedEvent += OnLevelFinished;
				}

				gameplayCoreSceneSetupData = BS_Utils.Plugin.LevelData.GameplayCoreSceneSetupData;

				// Register event listeners
				// public event Action GamePause#didPauseEvent;
				gamePause.didPauseEvent += OnGamePause;
				// public event Action GamePause#didResumeEvent;
				gamePause.didResumeEvent += OnGameResume;
				// public ScoreController#noteWasCutEvent<NoteData, NoteCutInfo, int multiplier> // called after AfterCutScoreBuffer is created
				scoreController.noteWasCutEvent += OnNoteWasCut;
				// public ScoreController#noteWasMissedEvent<NoteData, int multiplier>
				scoreController.noteWasMissedEvent += OnNoteWasMissed;
				// public ScoreController#scoreDidChangeEvent<int, int> // score
				scoreController.scoreDidChangeEvent += OnScoreDidChange;
				// public ScoreController#comboDidChangeEvent<int> // combo
				scoreController.comboDidChangeEvent += OnComboDidChange;
				// public ScoreController#multiplierDidChangeEvent<int, float> // multiplier, progress [0..1]
				scoreController.multiplierDidChangeEvent += OnMultiplierDidChange;
				// public event Action<BeatmapEventData> BeatmapObjectCallbackController#beatmapEventDidTriggerEvent
				beatmapObjectCallbackController.beatmapEventDidTriggerEvent += OnBeatmapEventDidTrigger;

				IDifficultyBeatmap diff = gameplayCoreSceneSetupData.difficultyBeatmap;
				IBeatmapLevel level = diff.level;
				CustomBeatmapLevel customLevel = (level is CustomBeatmapLevel custom) ? custom : null;

				gameStatus.partyMode = Gamemode.IsPartyActive;
				gameStatus.mode = Gamemode.GameMode;

				gameplayModifiers = gameplayCoreSceneSetupData.gameplayModifiers;
				PlayerSpecificSettings playerSettings = gameplayCoreSceneSetupData.playerSpecificSettings;
				PracticeSettings practiceSettings = gameplayCoreSceneSetupData.practiceSettings;

				PlayerLevelStatsData[] LevelStats = new PlayerLevelStatsData[5];

				PlayerDataModel PlayerData = Resources.FindObjectsOfTypeAll<PlayerDataModel>().First();
				if (PlayerData != null)
				{
					LevelStats[0] = diff.parentDifficultyBeatmapSet.difficultyBeatmaps.Where(x => x.difficulty == BeatmapDifficulty.Easy).Any() ? 
						PlayerData.playerData.GetPlayerLevelStatsData(level.levelID, BeatmapDifficulty.Easy, diff.parentDifficultyBeatmapSet.beatmapCharacteristic) :
						null;
					LevelStats[1] = diff.parentDifficultyBeatmapSet.difficultyBeatmaps.Where(x => x.difficulty == BeatmapDifficulty.Normal).Any() ? 
						PlayerData.playerData.GetPlayerLevelStatsData(level.levelID, BeatmapDifficulty.Normal, diff.parentDifficultyBeatmapSet.beatmapCharacteristic) :
						null;
					LevelStats[2] = diff.parentDifficultyBeatmapSet.difficultyBeatmaps.Where(x => x.difficulty == BeatmapDifficulty.Hard).Any() ? 
						PlayerData.playerData.GetPlayerLevelStatsData(level.levelID, BeatmapDifficulty.Hard, diff.parentDifficultyBeatmapSet.beatmapCharacteristic):
						null;
					LevelStats[3] = diff.parentDifficultyBeatmapSet.difficultyBeatmaps.Where(x => x.difficulty == BeatmapDifficulty.Expert).Any() ? 
						PlayerData.playerData.GetPlayerLevelStatsData(level.levelID, BeatmapDifficulty.Expert, diff.parentDifficultyBeatmapSet.beatmapCharacteristic) :
						null;
					LevelStats[4] = diff.parentDifficultyBeatmapSet.difficultyBeatmaps.Where(x => x.difficulty == BeatmapDifficulty.ExpertPlus).Any() ? 
						PlayerData.playerData.GetPlayerLevelStatsData(level.levelID, BeatmapDifficulty.ExpertPlus, diff.parentDifficultyBeatmapSet.beatmapCharacteristic) :
						null;
				}

				float songSpeedMul = gameplayModifiers.songSpeedMul;
				if (practiceSettings != null) songSpeedMul = practiceSettings.songSpeedMul;
				float modifierMultiplier = gameplayModifiersSO.GetTotalMultiplier(gameplayModifiers);

				gameStatus.songName = level.songName;
				gameStatus.songSubName = level.songSubName;
				gameStatus.songAuthorName = level.songAuthorName;
				gameStatus.levelAuthorName = level.levelAuthorName;
				gameStatus.songBPM = level.beatsPerMinute;
				gameStatus.noteJumpSpeed = diff.noteJumpMovementSpeed;
				// 13 is "custom_level_" and 40 is the magic number for the length of the SHA-1 hash
				gameStatus.songHash = level.levelID.StartsWith("custom_level_") && !level.levelID.EndsWith(" WIP") ? level.levelID.Substring(13, 40) : null;
				gameStatus.levelId = level.levelID;
				gameStatus.levelStats = LevelStats;
				gameStatus.levelFileLocation = customLevel?.customLevelPath;
				gameStatus.songTimeOffset = (long) (level.songTimeOffset * 1000f / songSpeedMul);
				gameStatus.length = (long) (level.beatmapLevelData.audioClip.length * 1000f / songSpeedMul);
				gameStatus.start = GetCurrentTime() - (long) (audioTimeSyncController.songTime * 1000f / songSpeedMul);
				if (practiceSettings != null) gameStatus.start -= (long) (practiceSettings.startSongTime * 1000f / songSpeedMul);
				gameStatus.songPosition = (long)(audioTimeSyncController.songTime * 1000f / songSpeedMul);
				gameStatus.paused = 0;
				gameStatus.difficulty = diff.difficulty.Name();
				gameStatus.notesCount = diff.beatmapData.notesCount;
				gameStatus.bombsCount = diff.beatmapData.bombsCount;
				gameStatus.obstaclesCount = diff.beatmapData.obstaclesCount;
				gameStatus.environmentName = level.environmentInfo.sceneInfo.sceneName;

				gameStatus.maxScore = gameplayModifiersSO.MaxModifiedScoreForMaxRawScore(ScoreModel.MaxRawScoreForNumberOfNotes(diff.beatmapData.notesCount), gameplayModifiers, gameplayModifiersSO);
				gameStatus.maxRank = RankModel.MaxRankForGameplayModifiers(gameplayModifiers, gameplayModifiersSO).ToString();

				try {
					// From https://support.unity3d.com/hc/en-us/articles/206486626-How-can-I-get-pixels-from-unreadable-textures-
					var texture = await level.GetCoverImageTexture2DAsync(CancellationToken.None);
					var active = RenderTexture.active;
					var temporary = RenderTexture.GetTemporary(
						texture.width,
						texture.height,
						0,
						RenderTextureFormat.Default,
						RenderTextureReadWrite.Linear
					);

					Graphics.Blit(texture, temporary);
					RenderTexture.active = temporary;

					var cover = new Texture2D(texture.width, texture.height);
					cover.ReadPixels(new Rect(0, 0, temporary.width, temporary.height), 0, 0);
					cover.Apply();

					RenderTexture.active = active;
					RenderTexture.ReleaseTemporary(temporary);

					gameStatus.songCover = System.Convert.ToBase64String(
						ImageConversion.EncodeToPNG(cover)
					);
				} catch {
					gameStatus.songCover = null;
				}

				gameStatus.ResetPerformance();

				gameStatus.modifierMultiplier = modifierMultiplier;
				gameStatus.songSpeedMultiplier = songSpeedMul;
				gameStatus.batteryLives = gameEnergyCounter.batteryLives;

				gameStatus.modObstacles = gameplayModifiers.enabledObstacleType.ToString();
				gameStatus.modInstaFail = gameplayModifiers.instaFail;
				gameStatus.modNoFail = gameplayModifiers.noFail;
				gameStatus.modBatteryEnergy = gameplayModifiers.batteryEnergy;
				gameStatus.modDisappearingArrows = gameplayModifiers.disappearingArrows;
				gameStatus.modNoBombs = gameplayModifiers.noBombs;
				gameStatus.modSongSpeed = gameplayModifiers.songSpeed.ToString();
				gameStatus.modNoArrows = gameplayModifiers.noArrows;
				gameStatus.modGhostNotes = gameplayModifiers.ghostNotes;
				gameStatus.modFailOnSaberClash = gameplayModifiers.failOnSaberClash;
				gameStatus.modStrictAngles = gameplayModifiers.strictAngles;
				gameStatus.modFastNotes = gameplayModifiers.fastNotes;

				gameStatus.staticLights = playerSettings.staticLights;
				gameStatus.leftHanded = playerSettings.leftHanded;
				gameStatus.playerHeight = playerSettings.playerHeight;
				gameStatus.sfxVolume = playerSettings.sfxVolume;
				gameStatus.reduceDebris = playerSettings.reduceDebris;
				gameStatus.noHUD = playerSettings.noTextsAndHuds;
				gameStatus.advancedHUD = playerSettings.advancedHud;
				gameStatus.autoRestart = playerSettings.autoRestart;

				statusManager.EmitStatusUpdate(ChangedProperties.AllButNoteCut, "songStart");
			}
		}

		private static T FindFirstOrDefault<T>() where T: UnityEngine.Object {
			T obj = Resources.FindObjectsOfTypeAll<T>().FirstOrDefault();
			if (obj == null) {
				Plugin.log.Error("Couldn't find " + typeof(T).FullName);
				throw new InvalidOperationException("Couldn't find " + typeof(T).FullName);
			}
			return obj;
		}

		public void OnUpdate() {
			bool currentHeadInObstacle = false;
			statusManager.gameStatus.songPosition = (long)(audioTimeSyncController.songTime * 1000f / statusManager.gameStatus.songSpeedMultiplier);

			if (playerHeadAndObstacleInteraction != null) {
				currentHeadInObstacle = playerHeadAndObstacleInteraction.intersectingObstacles.Count > 0;
			}

			if (!headInObstacle && currentHeadInObstacle) {
				headInObstacle = true;

				statusManager.EmitStatusUpdate(ChangedProperties.Performance, "obstacleEnter");
			} else if (headInObstacle && !currentHeadInObstacle) {
				headInObstacle = false;

				statusManager.EmitStatusUpdate(ChangedProperties.Performance, "obstacleExit");
			}
		}

		public void OnGamePause() {
			statusManager.gameStatus.paused = GetCurrentTime();
			statusManager.gameStatus.songPosition = (long)(audioTimeSyncController.songTime * 1000f / statusManager.gameStatus.songSpeedMultiplier);

			statusManager.EmitStatusUpdate(ChangedProperties.Beatmap, "pause");
		}

		public void OnGameResume() {
			statusManager.gameStatus.start = GetCurrentTime() - (long) (audioTimeSyncController.songTime * 1000f / statusManager.gameStatus.songSpeedMultiplier);
			statusManager.gameStatus.songPosition = (long)(audioTimeSyncController.songTime * 1000f / statusManager.gameStatus.songSpeedMultiplier);
			statusManager.gameStatus.paused = 0;

			statusManager.EmitStatusUpdate(ChangedProperties.Beatmap, "resume");
		}

		public void OnNoteWasCut(NoteData noteData, NoteCutInfo noteCutInfo, int multiplier) {
			// Event order: combo, multiplier, scoreController.noteWasCut, (LateUpdate) scoreController.scoreDidChange, afterCut, (LateUpdate) scoreController.scoreDidChange

			var gameStatus = statusManager.gameStatus;

			SetNoteCutStatus(noteData, noteCutInfo, true);

			int beforeCutScore = 0;
			int afterCutScore = 0;
			int cutDistanceScore = 0;

			ScoreModel.RawScoreWithoutMultiplier(noteCutInfo, out beforeCutScore, out afterCutScore, out cutDistanceScore);

			gameStatus.initialScore = beforeCutScore + cutDistanceScore;
			gameStatus.finalScore = -1;
			gameStatus.cutDistanceScore = cutDistanceScore;
			gameStatus.cutMultiplier = multiplier;
			gameStatus.songPosition = (long)(audioTimeSyncController.songTime * 1000f / statusManager.gameStatus.songSpeedMultiplier);

			if (noteData.noteType == NoteType.Bomb) {
				gameStatus.passedBombs++;
				gameStatus.hitBombs++;

				statusManager.EmitStatusUpdate(ChangedProperties.PerformanceAndNoteCut, "bombCut");
			} else {
				gameStatus.passedNotes++;

				if (noteCutInfo.allIsOK) {
					gameStatus.hitNotes++;

					statusManager.EmitStatusUpdate(ChangedProperties.PerformanceAndNoteCut, "noteCut");
				} else {
					gameStatus.missedNotes++;

					statusManager.EmitStatusUpdate(ChangedProperties.PerformanceAndNoteCut, "noteMissed");
				}
			}

			List<CutScoreBuffer> list = (List<CutScoreBuffer>) afterCutScoreBuffersField.GetValue(scoreController);

			foreach (CutScoreBuffer acsb in list) {
				if (noteCutInfoField.GetValue(acsb) == noteCutInfo) {
					// public CutScoreBuffer#didFinishEvent<CutScoreBuffer>
					noteCutMapping.Add(noteCutInfo, noteData);

					acsb.didFinishEvent += OnNoteWasFullyCut;
					break;
				}
			}
		}

		public void OnNoteWasFullyCut(CutScoreBuffer acsb) {
			int beforeCutScore;
			int afterCutScore;
			int cutDistanceScore;

			NoteCutInfo noteCutInfo = (NoteCutInfo) noteCutInfoField.GetValue(acsb);
			NoteData noteData = noteCutMapping[noteCutInfo];

			noteCutMapping.Remove(noteCutInfo);

			SetNoteCutStatus(noteData, noteCutInfo, false);

			// public static ScoreModel.RawScoreWithoutMultiplier(NoteCutInfo, out int beforeCutRawScore, out int afterCutRawScore, out int cutDistanceRawScore)
			ScoreModel.RawScoreWithoutMultiplier(noteCutInfo, out beforeCutScore, out afterCutScore, out cutDistanceScore);

			int multiplier = (int) cutScoreBufferMultiplierField.GetValue(acsb);

			statusManager.gameStatus.initialScore = beforeCutScore + cutDistanceScore;
			statusManager.gameStatus.finalScore = beforeCutScore + afterCutScore + cutDistanceScore;
			statusManager.gameStatus.cutDistanceScore = cutDistanceScore;
			statusManager.gameStatus.cutMultiplier = multiplier;
			statusManager.gameStatus.songPosition = (long)(audioTimeSyncController.songTime * 1000f / statusManager.gameStatus.songSpeedMultiplier);

			statusManager.EmitStatusUpdate(ChangedProperties.PerformanceAndNoteCut, "noteFullyCut");

			acsb.didFinishEvent -= OnNoteWasFullyCut;
		}

		private void SetNoteCutStatus(NoteData noteData, NoteCutInfo noteCutInfo = null, bool initialCut = true) {
			GameStatus gameStatus = statusManager.gameStatus;

			gameStatus.ResetNoteCut();

			gameStatus.noteID = noteData.id;
			gameStatus.noteType = noteData.noteType.ToString();
			gameStatus.noteCutDirection = noteData.cutDirection.ToString();
			gameStatus.noteLine = noteData.lineIndex;
			gameStatus.noteLayer = (int) noteData.noteLineLayer;
			gameStatus.timeToNextBasicNote = noteData.timeToNextBasicNote;

			if (noteCutInfo != null) {
				gameStatus.speedOK = noteCutInfo.speedOK;
				gameStatus.directionOK = noteCutInfo.directionOK;
				gameStatus.saberTypeOK = noteCutInfo.saberTypeOK;
				gameStatus.wasCutTooSoon = noteCutInfo.wasCutTooSoon;
				gameStatus.saberSpeed = noteCutInfo.saberSpeed;
				gameStatus.saberDirX = noteCutInfo.saberDir[0];
				gameStatus.saberDirY = noteCutInfo.saberDir[1];
				gameStatus.saberDirZ = noteCutInfo.saberDir[2];
				gameStatus.saberType = noteCutInfo.saberType.ToString();
				gameStatus.swingRating = noteCutInfo.swingRatingCounter == null ? -1 : initialCut ? noteCutInfo.swingRatingCounter.beforeCutRating : noteCutInfo.swingRatingCounter.afterCutRating;
				gameStatus.timeDeviation = noteCutInfo.timeDeviation;
				gameStatus.cutDirectionDeviation = noteCutInfo.cutDirDeviation;
				gameStatus.cutPointX = noteCutInfo.cutPoint[0];
				gameStatus.cutPointY = noteCutInfo.cutPoint[1];
				gameStatus.cutPointZ = noteCutInfo.cutPoint[2];
				gameStatus.cutNormalX = noteCutInfo.cutNormal[0];
				gameStatus.cutNormalY = noteCutInfo.cutNormal[1];
				gameStatus.cutNormalZ = noteCutInfo.cutNormal[2];
				gameStatus.cutDistanceToCenter = noteCutInfo.cutDistanceToCenter;
			}
		}

		public void OnNoteWasMissed(NoteData noteData, int multiplier) {
			// Event order: combo, multiplier, scoreController.noteWasMissed, (LateUpdate) scoreController.scoreDidChange

			statusManager.gameStatus.batteryEnergy = gameEnergyCounter.batteryEnergy;
			statusManager.gameStatus.songPosition = (long)(audioTimeSyncController.songTime * 1000f / statusManager.gameStatus.songSpeedMultiplier);

			SetNoteCutStatus(noteData);

			if (noteData.noteType == NoteType.Bomb) {
				statusManager.gameStatus.passedBombs++;

				statusManager.EmitStatusUpdate(ChangedProperties.PerformanceAndNoteCut, "bombMissed");
			} else {
				statusManager.gameStatus.passedNotes++;
				statusManager.gameStatus.missedNotes++;

				statusManager.EmitStatusUpdate(ChangedProperties.PerformanceAndNoteCut, "noteMissed");
			}
		}

		public void OnScoreDidChange(int scoreBeforeMultiplier, int scoreAfterMultiplier) {
			GameStatus gameStatus = statusManager.gameStatus;

			gameStatus.score = scoreAfterMultiplier;

			int currentMaxScoreBeforeMultiplier = ScoreModel.MaxRawScoreForNumberOfNotes(gameStatus.passedNotes);
			gameStatus.currentMaxScore = gameplayModifiersSO.MaxModifiedScoreForMaxRawScore(currentMaxScoreBeforeMultiplier, gameplayModifiers, gameplayModifiersSO);

			RankModel.Rank rank = RankModel.GetRankForScore(scoreBeforeMultiplier, gameStatus.score, currentMaxScoreBeforeMultiplier, gameStatus.currentMaxScore);
			gameStatus.rank = RankModel.GetRankName(rank);

			statusManager.EmitStatusUpdate(ChangedProperties.Performance, "scoreChanged");
		}

		public void OnComboDidChange(int combo) {
			statusManager.gameStatus.combo = combo;
			// public int ScoreController#maxCombo
			statusManager.gameStatus.maxCombo = scoreController.maxCombo;
		}

		public void OnMultiplierDidChange(int multiplier, float multiplierProgress) {
			statusManager.gameStatus.multiplier = multiplier;
			statusManager.gameStatus.multiplierProgress = multiplierProgress;
		}

		public void OnLevelFinished() {
			statusManager.EmitStatusUpdate(ChangedProperties.Performance, "finished");
		}

		public void OnLevelFailed() {
			statusManager.EmitStatusUpdate(ChangedProperties.Performance, "failed");
		}

		public void OnBeatmapEventDidTrigger(BeatmapEventData beatmapEventData) {
			statusManager.gameStatus.beatmapEventType = (int) beatmapEventData.type;
			statusManager.gameStatus.beatmapEventValue = beatmapEventData.value;

			statusManager.EmitStatusUpdate(ChangedProperties.BeatmapEvent, "beatmapEvent");
		}

		public static long GetCurrentTime() {
			return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
		}

		public class PluginTickerScript : PersistentSingleton<PluginTickerScript> {
			public void Update() {
				if (Plugin.instance != null) Plugin.instance.OnUpdate();
			}
		}
	}
}
