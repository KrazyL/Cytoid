﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Cytoid.Storyboard;
using UnityEngine;
using UniRx.Async;
using UnityEngine.Events;
using UnityEngine.Networking;

public class Game : MonoBehaviour
{
    public GameObject contentParent;
    public EffectController effectController;
    public InputController inputController;

    public bool IsLoaded { get; protected set; }

    public GameConfig Config { get; protected set; }
    public GameState State { get; protected set; }
    public GameRenderer Renderer { get; protected set; }

    public Level Level { get; protected set; }
    
    public Difficulty Difficulty { get; protected set; }
    public Chart Chart { get; protected set; }
    public Dictionary<int, Note> Notes { get; } = new Dictionary<int, Note>();

    public Storyboard Storyboard { get; protected set; }
    
    public float Time { get; protected set; }
    public float MusicDuration { get; protected set; }
    public float MusicStartedTimestamp { get; protected set; } // When was the music started to play?
    public float MusicStartedAt { get; protected set; } // When the music was played, from when was it played?
    public float MainMusicProgress { get; protected set; }
    public float UnpauseCountdown { get; protected set; }

    public AudioManager.Controller Music { get; protected set; }
    public AudioManager.Controller HitSound { get; protected set; }
    
    public List<UniTask> BeforeStartTasks { get; protected set; } = new List<UniTask>();
    public List<UniTask> BeforeExitTasks { get; protected set; } = new List<UniTask>();

    public GameEvent onGameReadyToLoad = new GameEvent();
    public GameEvent onGameLoaded = new GameEvent();
    public GameEvent onGameStarted = new GameEvent();
    public GameEvent onGameUpdate = new GameEvent();
    public GameEvent onGamePaused = new GameEvent();
    public GameEvent onGameWillUnpause = new GameEvent();
    public GameEvent onGameUnpaused = new GameEvent();
    public GameEvent onGameFailed = new GameEvent();
    public GameEvent onGameCompleted = new GameEvent();
    public GameEvent onGameReadyToExit = new GameEvent();
    public GameEvent onGameAborted = new GameEvent();
    public NoteEvent onNoteClear = new NoteEvent();
    public GameEvent onGameSpeedUp = new GameEvent();
    public GameEvent onGameSpeedDown = new GameEvent();
    public GameEvent onTopBoundaryBounded = new GameEvent();
    public GameEvent onBottomBoundaryBounded = new GameEvent();

    protected void Awake()
    {
        Renderer = new GameRenderer(this);
    }

    protected async void Start()
    {
        await Initialize(Context.SelectedLevel, Context.SelectedDifficulty);
    }

    public async UniTask Initialize(Level level, Difficulty difficulty, bool startAutomatically = true)
    {
        Level = level;
        Difficulty = difficulty;

        onGameReadyToLoad.Invoke(this);

        // Load chart
        print("Loading chart");
        var chartMeta = level.Meta.GetChartSection(Difficulty.Id);
        var chartPath = "file://" + level.Path + chartMeta.path;
        string chartText;
        using (var request = UnityWebRequest.Get(chartPath))
        {
            await request.SendWebRequest();
            if (request.isNetworkError || request.isHttpError)
            {
                Debug.LogError($"Failed to download chart from {chartPath}");
                Debug.LogError(request.error);
                return;
            }

            chartText = Encoding.UTF8.GetString(request.downloadHandler.data);
        }

        var mods = Context.LocalPlayer.EnabledMods;
        Chart = new Chart(
            chartText,
            mods.Contains(Mod.FlipX) || mods.Contains(Mod.FlipAll),
            mods.Contains(Mod.FlipY) || mods.Contains(Mod.FlipAll),
            true,
            mods.Contains(Mod.Fast) ? 1.5f : (mods.Contains(Mod.Slow) ? 0.75f : 1),
            0.8f + (5 - (int) Context.LocalPlayer.HorizontalMargin - 1) * 0.025f,
            (5.5f + (5 - (int) Context.LocalPlayer.VerticalMargin) * 0.5f) / 9.0f
        );

        // Load audio
        print("Loading audio");

        var audioPath = "file://" + Level.Path + Level.Meta.GetMusicPath(difficulty.Id);
        var loader = new AssetLoader(audioPath);
        await loader.LoadAudioClip();
        if (loader.Error != null)
        {
            Debug.LogError($"Failed to download audio from {audioPath}");
            Debug.LogError(loader.Error);
            return;
        }
        
        MusicDuration = loader.AudioClip.length;
        
        if (Context.AudioManager == null) await UniTask.WaitUntil(() => Context.AudioManager != null);
        Music = Context.AudioManager.Load("Level", loader.AudioClip);
        
        // Load storyboard
        var storyboardPath =
            level.Path + (chartMeta.storyboard != null ? chartMeta.storyboard.path : "storyboard.json");

        if (File.Exists(storyboardPath)) {
            // Initialize storyboard
            // TODO: Why File.ReadAllText() works but not UnityWebRequest?
            // (UnityWebRequest downloaded text could not be parsed by Newtonsoft.Json)
            var storyboardText = File.ReadAllText(storyboardPath);
            Storyboard = new Storyboard(this, storyboardText);
            await Storyboard.Initialize();
        }

        // Load hit sound
        Context.LocalPlayer.HitSound = "none";
        if (Context.LocalPlayer.HitSound != "none")
        {
            var resource = await Resources.LoadAsync<AudioClip>("Audio/HitSounds/" + Context.LocalPlayer.HitSound);
            HitSound = Context.AudioManager.Load("HitSound", resource as AudioClip);
        }

        // State & config
        var isRanked = Context.LocalPlayer.PlayRanked && Context.OnlinePlayer.IsAuthenticated;
        var maxHealth = chartMeta.difficulty * 75;
        if (maxHealth < 0) maxHealth = 1000;
        State = new GameState(this, isRanked, mods, maxHealth);
        Config = new GameConfig(this);

        // Touch handlers
        if (!mods.Contains(Mod.Auto))
        {
            inputController.EnableInput();
        }

        // System config
        Application.targetFrameRate = 120;
        Context.SetAutoRotation(false);

        IsLoaded = true;
        onGameLoaded.Invoke(this);
        
        Context.ScreenManager.ChangeScreen("Overlay", ScreenTransition.None);
        
        if (startAutomatically)
        {
            StartGame();
        }
    }

    protected async void StartGame()
    {
        await UniTask.WhenAll(BeforeStartTasks);
        
        Context.ScreenManager.ChangeScreen(OverlayScreen.Id, ScreenTransition.None);
        
        Music.Play(AudioTrackIndex.Reserved1);

        await UniTask.WaitUntil(() =>
        {
            // Wait until the audio actually starts playing
            var playbackTime = Music.PlaybackTime;
            return playbackTime > 0 && playbackTime < MusicDuration;
        });

        MusicStartedTimestamp = UnityEngine.Time.realtimeSinceStartup;
        MusicStartedAt = Music.PlaybackTime;

        State.IsStarted = true;
        State.IsPlaying = true;
        onGameStarted.Invoke(this);
    }

    protected virtual void Update()
    {
        if (!IsLoaded) return;
        
        Renderer.OnUpdate();
        
        if (!State.IsPlaying) return;

        if (Input.GetKeyDown(KeyCode.Escape) && !(this is StoryboardGame))
        {
            Pause();
            return;
        }

        if (State.ShouldFail) Fail();
        if (State.IsFailed) Music.PlaybackTime -= 1f / 120f;
        if (State.IsPlaying)
        {
            if (State.ClearCount >= Chart.Model.note_list.Count) Complete();

            // Update current states
            if (this is StoryboardGame)
            {
                // PlaybackTime is accurate enough on desktop
                Time = Music.PlaybackTime - Config.ChartOffset + Chart.MusicOffset + MusicStartedAt;
            }
            else
            {
                Time = UnityEngine.Time.realtimeSinceStartup - MusicStartedTimestamp + MusicStartedAt
                       - Config.ChartOffset + Chart.MusicOffset;
            }

            MainMusicProgress = Time / MusicDuration;

            // Process chart elements
            while (Chart.CurrentEventId < Chart.Model.event_order_list.Count &&
                   Chart.Model.event_order_list[Chart.CurrentEventId].time < Time)
            {
                if (Chart.Model.event_order_list[Chart.CurrentEventId].event_list[0].type == 0)
                {
                    onGameSpeedUp.Invoke(this);
                }
                else
                {
                    onGameSpeedDown.Invoke(this);
                }

                Chart.CurrentEventId++;
            }

            while (Chart.CurrentPageId < Chart.Model.page_list.Count &&
                   Chart.Model.page_list[Chart.CurrentPageId].end_time <= Time)
            {
                if (Chart.Model.page_list[Chart.CurrentPageId].scan_line_direction == 1)
                {
                    if (!State.IsCompleted) onTopBoundaryBounded.Invoke(this);
                }
                else
                {
                    if (!State.IsCompleted) onBottomBoundaryBounded.Invoke(this);
                }

                Chart.CurrentPageId++;
            }

            var notes = Chart.Model.note_list;
            while (Chart.CurrentNoteId < notes.Count && notes[Chart.CurrentNoteId].start_time - 2.0f < Time)
                switch ((NoteType) notes[Chart.CurrentNoteId].type)
                {
                    case NoteType.DragHead:
                        var id = Chart.CurrentNoteId;
                        while (notes[id].next_id > 0)
                        {
                            SpawnDragLine(notes[id], notes[notes[id].next_id]);
                            id = notes[id].next_id;
                        }

                        SpawnNote(notes[Chart.CurrentNoteId]);
                        Chart.CurrentNoteId++;
                        break;
                    default:
                        SpawnNote(notes[Chart.CurrentNoteId]);
                        Chart.CurrentNoteId++;
                        break;
                }
        }

        onGameUpdate.Invoke(this);
    }

    public virtual void SpawnNote(ChartModel.Note model)
    {
        var provider = GameObjectProvider.Instance;
        Note note;
        switch ((NoteType) model.type)
        {
            case NoteType.Click:
                note = Instantiate(provider.clickNotePrefab, contentParent.transform).GetComponent<Note>();
                break;
            case NoteType.Hold:
                note = Instantiate(provider.holdNotePrefab, contentParent.transform).GetComponent<Note>();
                break;
            case NoteType.LongHold:
                note = Instantiate(provider.longHoldNotePrefab, contentParent.transform).GetComponent<Note>();
                break;
            case NoteType.DragHead:
                note = Instantiate(provider.dragHeadNotePrefab, contentParent.transform).GetComponent<Note>();
                break;
            case NoteType.DragChild:
                note = Instantiate(provider.dragChildNotePrefab, contentParent.transform).GetComponent<Note>();
                break;
            case NoteType.Flick:
                note = Instantiate(provider.flickNotePrefab, contentParent.transform).GetComponent<Note>();
                break;
            default:
                note = Instantiate(provider.clickNotePrefab, contentParent.transform).GetComponent<Note>();
                break;
        }

        note.SetData(this, model.id);
        Notes[model.id] = note;
    }

    public virtual void SpawnDragLine(ChartModel.Note from, ChartModel.Note to)
    {
        var dragLineView = Instantiate(GameObjectProvider.Instance.dragLinePrefab, contentParent.transform)
            .GetComponent<DragLineElement>();
        dragLineView.SetData(this, from, to);
    }

    protected virtual void OnApplicationPause(bool willPause)
    {
        if (IsLoaded && State.IsStarted && willPause)
        {
            Pause();
            Context.ScreenManager.ChangeScreen(PausedScreen.Id, ScreenTransition.None);
        }
    }

    public void Pause()
    {
        unpause?.Cancel();
        UnpauseCountdown = 0;
        if (!IsLoaded || !State.IsPlaying || State.IsCompleted || State.IsFailed) return;
        print("Game paused");
        
        State.IsPlaying = false;
        Music.Pause();
        
        onGamePaused.Invoke(this);
    }

    private CancellationTokenSource unpause;

    public async void WillUnpause()
    {
        if (!IsLoaded || State.IsPlaying || State.IsCompleted || State.IsFailed || UnpauseCountdown > 0) return;
        print("Game ready to unpause");
        
        onGameWillUnpause.Invoke(this);

        UnpauseCountdown = 3;
        while (UnpauseCountdown > 0)
        {
            unpause = new CancellationTokenSource();
            try
            {
                await UniTask.Delay(TimeSpan.FromSeconds(0.1), cancellationToken: unpause.Token);
            }
            catch
            {
                print("Game unpause cancelled");
                return;
            }

            UnpauseCountdown -= 0.1f;
        }

        Unpause();
    }

    public virtual async void Unpause()
    {
        if (!IsLoaded || State.IsPlaying || State.IsCompleted || State.IsFailed) return;
        print("Game unpaused");
        
        Music.Resume();

        await UniTask.WaitUntil(() =>
        {
            // Wait until the audio actually starts playing
            var playbackTime = Music.PlaybackTime;
            return playbackTime > 0 && playbackTime < MusicDuration;
        });

        MusicStartedTimestamp = UnityEngine.Time.realtimeSinceStartup;
        MusicStartedAt = Music.PlaybackTime;

        State.IsPlaying = true;
        
        onGameUnpaused.Invoke(this);
    }

    public void Fail()
    {
        if (State.IsFailed) return;
        State.IsFailed = true;
        inputController.DisableInput();

        onGameFailed.Invoke(this);
        // TODO: Show fail UI
    }

    public virtual async void Complete()
    {
        if (State.IsCompleted || State.IsFailed) return;
        print("Game completed");

        State.IsCompleted = true;
        inputController.DisableInput();

        onGameCompleted.Invoke(this);
        
        // Wait for audio to finish
        await UniTask.WaitUntil(() => Music.IsFinished());
        print("Audio ended");
        
        await UniTask.WhenAll(BeforeExitTasks);

        onGameReadyToExit.Invoke(this);
        
        var sceneLoader = new SceneLoader("Navigation");
        sceneLoader.Load();

        await UniTask.Delay(TimeSpan.FromSeconds(1.5f));
        
        if (State.Mods.Contains(Mod.Auto) || State.Mods.Contains(Mod.AutoDrag)
                                          || State.Mods.Contains(Mod.AutoHold) || State.Mods.Contains(Mod.AutoFlick))
        {
            // TODO: Go back
        }
        else
        {
            Context.LastGameResult = new GameResult
            {
                Score = (int) State.Score,
                Accuracy = State.Accuracy,
                MaxCombo = State.MaxCombo,
                Mods = new List<Mod>(State.Mods),
                GradeCounts = new Dictionary<NoteGrade, int>
                {
                    {NoteGrade.Perfect, State.Judgements.Count(it => it.Value.Grade == NoteGrade.Perfect)},
                    {NoteGrade.Great, State.Judgements.Count(it => it.Value.Grade == NoteGrade.Great)},
                    {NoteGrade.Good, State.Judgements.Count(it => it.Value.Grade == NoteGrade.Good)},
                    {NoteGrade.Bad, State.Judgements.Count(it => it.Value.Grade == NoteGrade.Bad)},
                    {NoteGrade.Miss, State.Judgements.Count(it => it.Value.Grade == NoteGrade.Miss)}
                },
                EarlyCount = State.EarlyCount,
                LateCount = State.LateCount,
                AverageTimingError = State.AverageTimingError,
                StandardTimingError = State.StandardTimingError,
                LevelId = Level.Meta.id,
                LevelVersion = Level.Meta.version,
                ChartType = Context.SelectedDifficulty
            };
        }
        
        sceneLoader.Activate();
    }

}

public class GameEvent : UnityEvent<Game>
{
}

public class NoteEvent : UnityEvent<Game, Note>
{
}