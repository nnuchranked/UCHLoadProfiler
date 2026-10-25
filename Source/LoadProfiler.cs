using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using System.Threading;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;

[BepInPlugin("com.you.uch.loadprofiler", "UCH Load Profiler", "0.2.0")]
public class LoadProfiler : BaseUnityPlugin
{
    class Entry
    {
        public string Name;
        public long FrameTicks, TotalTicks;
        public int FrameCalls, TotalCalls, FrameStamp = -1;
    }

    const int MaxDepth = 512;
    static readonly System.Diagnostics.Stopwatch Clock = System.Diagnostics.Stopwatch.StartNew();
    static readonly Dictionary<long, Entry> Map = new Dictionary<long, Entry>();
    static readonly List<Entry> All = new List<Entry>();
    static readonly List<Entry> FrameList = new List<Entry>();
    static readonly long[] StStart = new long[MaxDepth];
    static readonly long[] StChild = new long[MaxDepth];
    static readonly Entry[] StEntry = new Entry[MaxDepth];
    static int depth, frameNo, mainThread, lastGc, reportsLeft, maxReports, sliceMs;
    static long frameSumTicks, slowTicks, tEarly, tLate, tEnd, armedAt;
    static float thresholdSec;
    static volatile bool capturing;
    static bool patching, patched;
    static ManualLogSource Log;
    static Harmony harmony;
    static string[] wholeClasses, extraNames;

    static string T() { return "[t=" + Clock.Elapsed.TotalSeconds.ToString("F2") + "s]"; }
    static string[] Split(string s) { return s.Split(new char[] { ',' }, StringSplitOptions.RemoveEmptyEntries); }

    void Awake()
    {
        Log = Logger;
        mainThread = Thread.CurrentThread.ManagedThreadId;
        harmony = new Harmony("com.you.uch.loadprofiler");
        thresholdSec = Config.Bind("Profiler", "FrameThresholdMs", 300f, "Log a breakdown for every frame that takes at least this many milliseconds.").Value / 1000f;
        slowTicks = (long)(Config.Bind("Profiler", "SlowCallMs", 250f, "Log any single instrumented call whose inclusive time reaches this many milliseconds.").Value * 10000f);
        maxReports = Config.Bind("Profiler", "MaxFrameReports", 25, "Maximum frame breakdowns logged before you press N (N resets the counter).").Value;
        sliceMs = Config.Bind("Profiler", "SliceMs", 40, "Milliseconds of patching per frame while arming.").Value;
        wholeClasses = Split(Config.Bind("Profiler", "WholeClasses", "QuickSaver,GameControl,AttachmentGroup,ColliderModeControl,SortOrder,PlaceableMetadataList,LobbyManager,GameState,SevenZipHelper",
            "Class names whose every method gets instrumented.").Value);
        extraNames = Split(Config.Bind("Profiler", "ExtraMethodNames", "Enable,Disable,Reset,Place,EnablePlaced,EnablePlacement,ToPlayMode,ToPlaceMode,SwitchToMode,SwitchColliderTo,SetColor,Tint,UpdateSortOrder",
            "Extra method names instrumented on every game script.").Value);
        harmony.Patch(AccessTools.DeclaredMethod(typeof(GameEvent.GameEventManager), "SendEvent"), new HarmonyMethod(typeof(LoadProfiler), "OnEvent"));
        gameObject.AddComponent<LoadProfilerEarly>();
        gameObject.AddComponent<LoadProfilerLate>();
        StartCoroutine(EndOfFrameLoop());
        SceneManager.sceneLoaded += OnSceneLoaded;
        Logger.LogInfo("LoadProfiler ready. P = instrument and start capturing (do this BEFORE loading a level), N = print totals, M = marker.");
    }

    static void OnSceneLoaded(Scene s, LoadSceneMode m) { Log.LogInfo(T() + " scene loaded: " + s.name); }

    void Update()
    {
        if (Input.GetKeyDown(KeyCode.M)) Log.LogInfo(T() + " MARK");
        if (Input.GetKeyDown(KeyCode.P)) Arm();
        if (Input.GetKeyDown(KeyCode.N)) Summary();
    }

    void Arm()
    {
        if (patching) { Log.LogInfo(T() + " still instrumenting, please wait."); return; }
        if (patched)
        {
            capturing = !capturing;
            Log.LogInfo(T() + " capture " + (capturing ? "ON" : "OFF"));
            if (capturing) { armedAt = Clock.Elapsed.Ticks; lastGc = GC.CollectionCount(0); reportsLeft = maxReports; depth = 0; FrameList.Clear(); frameSumTicks = 0; }
            return;
        }
        patching = true;
        StartCoroutine(PatchAll());
    }

    IEnumerator PatchAll()
    {
        List<MethodBase> targets = new List<MethodBase>();
        Collect(typeof(Placeable).Assembly, targets, true);
        Collect(typeof(Smooth.State).Assembly, targets, false);
        Log.LogInfo(T() + " LoadProfiler: instrumenting " + targets.Count + " methods in slices (the game stays playable)...");
        long began = Clock.Elapsed.Ticks;
        long sliceStart = began;
        long sliceTicks = (long)sliceMs * 10000L;
        HarmonyMethod pre = new HarmonyMethod(typeof(LoadProfiler), "Pre");
        HarmonyMethod fin = new HarmonyMethod(typeof(LoadProfiler), "Fin");
        int ok = 0, fail = 0;
        for (int i = 0; i < targets.Count; i++)
        {
            MethodBase m = targets[i];
            try
            {
                Entry e = new Entry();
                e.Name = NameOf(m);
                harmony.Patch(m, pre, null, null, fin, null);
                Map[m.MethodHandle.Value.ToInt64()] = e;
                All.Add(e);
                ok++;
            }
            catch (Exception) { fail++; }
            if (Clock.Elapsed.Ticks - sliceStart > sliceTicks) { yield return null; sliceStart = Clock.Elapsed.Ticks; }
        }
        patched = true;
        patching = false;
        depth = 0;
        FrameList.Clear();
        frameSumTicks = 0;
        armedAt = Clock.Elapsed.Ticks;
        reportsLeft = maxReports;
        lastGc = GC.CollectionCount(0);
        capturing = true;
        Log.LogInfo(T() + " LoadProfiler ARMED: " + ok + " methods instrumented (" + fail + " skipped) in " + ((Clock.Elapsed.Ticks - began) / 10000000.0).ToString("F1") + " s. Capturing now. Load your level.");
    }

    static void Collect(Assembly asm, List<MethodBase> list, bool lifecycle)
    {
        HashSet<string> unity = new HashSet<string>(new string[] { "Awake", "Start", "OnEnable", "OnDisable", "OnDestroy", "Update", "LateUpdate", "FixedUpdate" });
        HashSet<string> events = new HashSet<string>(new string[] { "handleEvent", "ReceiveEvent" });
        HashSet<string> extra = new HashSet<string>(extraNames);
        HashSet<string> whole = new HashSet<string>(wholeClasses);
        HashSet<long> seen = new HashSet<long>();
        Type[] types;
        try { types = asm.GetTypes(); }
        catch (ReflectionTypeLoadException e) { types = e.Types; }
        BindingFlags flags = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        for (int i = 0; i < types.Length; i++)
        {
            Type t = types[i];
            if (t == null || t.ContainsGenericParameters) continue;
            try
            {
                bool behaviour = typeof(Component).IsAssignableFrom(t);
                bool isWhole = whole.Contains(t.Name);
                bool iterator = lifecycle && t.IsNested && t.Name.IndexOf(">d__", StringComparison.Ordinal) > 0 && typeof(IEnumerator).IsAssignableFrom(t) &&
                    (typeof(Component).IsAssignableFrom(t.DeclaringType) || whole.Contains(t.DeclaringType.Name));
                if (!isWhole && !iterator && !lifecycle) continue;
                foreach (MethodInfo m in t.GetMethods(flags))
                {
                    if (m.IsAbstract || m.IsGenericMethod || m.ContainsGenericParameters) continue;
                    string n = m.Name;
                    bool take;
                    if (iterator) take = n == "MoveNext";
                    else if (isWhole) take = !n.StartsWith("get_", StringComparison.Ordinal) && !n.StartsWith("set_", StringComparison.Ordinal) && n.IndexOf('<') < 0;
                    else take = lifecycle && ((behaviour && (unity.Contains(n) || extra.Contains(n))) || events.Contains(n));
                    if (!take) continue;
                    if (m.GetMethodBody() == null) continue;
                    if (!seen.Add(m.MethodHandle.Value.ToInt64())) continue;
                    list.Add(m);
                }
            }
            catch (Exception) { }
        }
    }

    static string NameOf(MethodBase m)
    {
        Type t = m.DeclaringType;
        if (t.IsNested && t.Name.IndexOf(">d__", StringComparison.Ordinal) > 0)
        {
            int end = t.Name.IndexOf('>');
            return t.DeclaringType.Name + "." + t.Name.Substring(1, end - 1) + " (coroutine)";
        }
        return t.Name + "." + m.Name;
    }

    public static void Pre(MethodBase __originalMethod, out bool __state)
    {
        __state = false;
        if (!capturing) return;
        if (Thread.CurrentThread.ManagedThreadId != mainThread) return;
        Entry e;
        if (!Map.TryGetValue(__originalMethod.MethodHandle.Value.ToInt64(), out e)) return;
        if (depth >= MaxDepth) return;
        StEntry[depth] = e;
        StChild[depth] = 0;
        StStart[depth] = Clock.Elapsed.Ticks;
        depth++;
        __state = true;
    }

    public static Exception Fin(bool __state, Exception __exception)
    {
        if (__state && depth > 0)
        {
            long end = Clock.Elapsed.Ticks;
            depth--;
            long total = end - StStart[depth];
            long excl = total - StChild[depth];
            Entry e = StEntry[depth];
            StEntry[depth] = null;
            if (e.FrameStamp != frameNo) { e.FrameStamp = frameNo; e.FrameTicks = 0; e.FrameCalls = 0; FrameList.Add(e); }
            e.FrameTicks += excl;
            e.FrameCalls++;
            e.TotalTicks += excl;
            e.TotalCalls++;
            frameSumTicks += excl;
            if (depth > 0) StChild[depth - 1] += total;
            if (total >= slowTicks)
                Log.LogInfo(string.Format("{0} slow call: {1} inclusive {2:F0} ms, exclusive {3:F0} ms (nesting {4})", T(), e.Name, total / 10000.0, excl / 10000.0, depth));
        }
        return __exception;
    }

    public static void OnEvent(object __0)
    {
        if (__0 == null) return;
        string n = __0.GetType().Name;
        if (n == "StartPhaseEvent" || n == "EndPhaseEvent" || n == "GameStartEvent" || n == "LevelResetEvent" || n == "PlayersDoneRunning" || n == "QuicksaverLevelFinishedLoading")
            Log.LogInfo(T() + " event " + n);
    }

    IEnumerator EndOfFrameLoop()
    {
        WaitForEndOfFrame wait = new WaitForEndOfFrame();
        while (true) { yield return wait; tEnd = Clock.Elapsed.Ticks; }
    }

    public static void OnLate() { tLate = Clock.Elapsed.Ticks; }

    // Runs first in every frame's Update phase: closes the previous frame.
    public static void OnEarly()
    {
        long now = Clock.Elapsed.Ticks;
        if (capturing)
        {
            float dt = Time.unscaledDeltaTime;
            if (tEnd > 0 && dt >= thresholdSec && reportsLeft > 0) { reportsLeft--; Report(dt, now); }
            FrameList.Clear();
            frameSumTicks = 0;
            frameNo++;
        }
        tEarly = now;
    }

    static void Report(float dt, long now)
    {
        double frameMs = dt * 1000.0;
        double instr = frameSumTicks / 10000.0;
        StringBuilder sb = new StringBuilder();
        sb.Append(T()).Append(" FRAME ").Append(frameMs.ToString("F0")).Append(" ms");
        if (tLate >= tEarly && tEnd >= tLate)
            sb.Append(" | update+coroutines+animators ").Append(((tLate - tEarly) / 10000.0).ToString("F0"))
              .Append(" ms, lateupdate+render ").Append(((tEnd - tLate) / 10000.0).ToString("F0"))
              .Append(" ms, between-frames(physics/present) ").Append(((now - tEnd) / 10000.0).ToString("F0")).Append(" ms");
        sb.Append(" | instrumented ").Append(instr.ToString("F0")).Append(" ms, UNATTRIBUTED ").Append((frameMs - instr).ToString("F0")).Append(" ms");
        int gc = GC.CollectionCount(0);
        sb.Append(" | GC runs ").Append(gc - lastGc);
        lastGc = gc;
        Log.LogInfo(sb.ToString());
        FrameList.Sort(delegate (Entry a, Entry b) { return b.FrameTicks.CompareTo(a.FrameTicks); });
        for (int i = 0; i < FrameList.Count && i < 12; i++)
            Log.LogInfo(string.Format("      {0}: {1:F0} ms exclusive, {2} calls", FrameList[i].Name, FrameList[i].FrameTicks / 10000.0, FrameList[i].FrameCalls));
    }

    static void Summary()
    {
        if (All.Count == 0) { Log.LogInfo(T() + " LoadProfiler: not armed yet (press P first)."); return; }
        List<Entry> list = new List<Entry>(All);
        list.Sort(delegate (Entry a, Entry b) { return b.TotalTicks.CompareTo(a.TotalTicks); });
        double elapsed = (Clock.Elapsed.Ticks - armedAt) / 10000000.0;
        Log.LogInfo(T() + " TOTALS over " + elapsed.ToString("F1") + " s (exclusive time per method, top 30):");
        for (int i = 0; i < list.Count && i < 30; i++)
        {
            if (list[i].TotalTicks <= 0) break;
            Log.LogInfo(string.Format("   {0}: {1:F0} ms, {2} calls, {3:F1} us/call", list[i].Name, list[i].TotalTicks / 10000.0, list[i].TotalCalls, list[i].TotalTicks / 10.0 / Math.Max(1, list[i].TotalCalls)));
        }
        for (int i = 0; i < All.Count; i++) { All[i].TotalTicks = 0; All[i].TotalCalls = 0; }
        armedAt = Clock.Elapsed.Ticks;
        reportsLeft = maxReports;
    }
}

[DefaultExecutionOrder(-32000)]
public class LoadProfilerEarly : MonoBehaviour
{
    void Update() { LoadProfiler.OnEarly(); }
}

[DefaultExecutionOrder(32000)]
public class LoadProfilerLate : MonoBehaviour
{
    void LateUpdate() { LoadProfiler.OnLate(); }
}