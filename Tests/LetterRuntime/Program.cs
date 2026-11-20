// Real-runtime red/green for the swallowed-letter-button bug.
//
// The artifact under test is the shipped Source/LetterChoicePatch.cs (compiled unchanged, source-linked below).
// Everything it touches is the *real* game implementation from
// C:/SteamLibrary/steamapps/common/RimWorld/Assembly-CSharp.dll:
//   ChoiceLetter.OpenLetter -> DiaNode + Dialog_NodeTreeWithFactionInfo + Find.WindowStack.Add
//   DiaOption.Activate       -> Window.Close -> WindowStack.TryRemove -> PreClose/PostClose
// Only these boundaries are intercepted, because a headless process has no colony, no audio and no UIRoot:
//   * Find.WindowStack / Find.LetterStack getters  -> real WindowStack/LetterStack instances we own
//   * Log.Message/Warning/Error                     -> captured instead of printed
// The transport publishers and the two session flags are stubbed in TransportStubs.cs.
//
// The assertions describe the intended behaviour, so this program is red on the current patch and green after it.
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using HarmonyLib;
using RimWorld;
using Verse;
using TurboTick;

internal static class Program
{
    private const string GameDir = "C:/SteamLibrary/steamapps/common/RimWorld";
    private static readonly List<string> GameLogs = [];
    private static readonly FieldInfo CurNodeField = AccessTools.Field(typeof(Dialog_NodeTree), "curNode")!;
    private static readonly MethodInfo ActivateMethod = AccessTools.Method(typeof(DiaOption), "Activate")!;
    private static WindowStack Stack = null!;
    private static LetterStack Letters = null!;
    private static int Passed;
    private static int Failed;

    public static int Main()
    {
        AssemblyLoadContext.Default.Resolving += (_, name) =>
        {
            foreach (var dir in new[] { GameDir, GameDir + "/Mods/Harmony/1.7/Assemblies" })
            {
                var path = Path.Combine(dir, name.Name + ".dll");
                if (File.Exists(path)) return AssemblyLoadContext.Default.LoadFromAssemblyPath(path);
            }
            return null;
        };
        try { return Run(); }
        catch (Exception error) { Console.WriteLine(error); return 2; }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int Run()
    {
        var harmony = new Harmony("turbotick.letter.runtime.tests");
        foreach (var name in new[] { "Message", "Warning", "Error" })
            foreach (var method in typeof(Log).GetMethods(BindingFlags.Public | BindingFlags.Static)
                         .Where(m => m.Name == name && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType == typeof(string)))
                harmony.Patch(method, prefix: new HarmonyMethod(typeof(Program), nameof(CaptureLog)));
        harmony.Patch(AccessTools.PropertyGetter(typeof(Find), nameof(Find.WindowStack)),
            prefix: new HarmonyMethod(typeof(Program), nameof(WindowStackGetter)));
        harmony.Patch(AccessTools.PropertyGetter(typeof(Find), nameof(Find.LetterStack)),
            prefix: new HarmonyMethod(typeof(Program), nameof(LetterStackGetter)));
        // The shipped patch points, applied exactly as PatchInstaller does in the game.
        harmony.PatchAll(typeof(LetterOpenPatch).Assembly);

        Stack = new WindowStack();
        Letters = new LetterStack();
        Console.WriteLine($"RUNTIME {typeof(ChoiceLetter).Assembly.Location}");
        Console.WriteLine($"RUNTIME gameVersion={VersionFromGame()}");

        ChoiceLetterClickPath();
        UnrelatedDialogPath();
        NavigationPath();
        PostponePath();
        CoveragePath();

        Console.WriteLine($"SUMMARY passed={Passed} failed={Failed} sent={SharedActions.LetterChoices.Count} " +
                          $"refused={LetterChoiceState.ChoicesRefused} last={LetterChoiceState.LastRefusal ?? "-"} " +
                          $"gameLogs={GameLogs.Count}");
        foreach (var line in GameLogs) Console.WriteLine("GAME " + line);
        Console.WriteLine(Failed == 0 ? "RESULT PASS" : "RESULT FAIL");
        return Failed == 0 ? 0 : 1;
    }

    /// <summary>Clicking the option the dialog actually drew must report (letter, index), must not run the local
    /// action, must close the dialog, and must leave the letter in the stack for the host's removal to arrive.</summary>
    private static void ChoiceLetterClickPath()
    {
        var letter = new TestChoiceLetter { ID = 41 };
        Letters.LettersListForReading.Add(letter);
        letter.OpenLetter();

        var dialog = Stack.Count == 1 ? Stack[0] as Dialog_NodeTree : null;
        Check("click:dialog-opened", dialog != null, $"windows={Stack.Count}");
        if (dialog == null) return;
        var root = RootOf(dialog);
        Check("click:captured-actual-options", LetterChoiceState.CapturedCount == 2,
            $"captured={LetterChoiceState.CapturedCount} drawn={root.options.Count}");

        var accept = root.options[1];
        var sentBefore = SharedActions.LetterChoices.Count;
        // A real click: Widgets.ButtonText calls DiaOption.Activate, which the shipped prefix intercepts.
        ActivateMethod.Invoke(accept, null);

        Check("click:published-letter-and-index",
            SharedActions.LetterChoices.Count == sentBefore + 1 && SharedActions.LetterChoices[^1] == (41, 1),
            $"sentDelta={SharedActions.LetterChoices.Count - sentBefore} " +
            $"last={(SharedActions.LetterChoices.Count > 0 ? SharedActions.LetterChoices[^1].ToString() : "-")}");
        Check("click:action-not-run-locally", letter.AcceptRuns == 0, $"acceptRuns={letter.AcceptRuns}");
        Check("click:dialog-closed", Stack.Count == 0, $"windows={Stack.Count}");
        Check("click:letter-retained-for-server-update", Letters.LettersListForReading.Contains(letter),
            $"letters={Letters.LettersListForReading.Count}");
    }

    /// <summary>An option that belongs to an unrelated dialog must fall through to vanilla untouched.</summary>
    private static void UnrelatedDialogPath()
    {
        var node = new DiaNode("other dialog");
        var unrelatedRuns = 0;
        var ok = new DiaOption("OK") { resolveTree = true };
        ok.action = () => unrelatedRuns++;
        node.options.Add(ok);
        var other = new Dialog_NodeTree(node);
        Stack.Add(other);

        ActivateMethod.Invoke(ok, null);
        Check("unrelated:not-intercepted", unrelatedRuns == 1, $"runs={unrelatedRuns}");
        Check("unrelated:dialog-closed-by-vanilla", Stack.Count == 0, $"windows={Stack.Count}");
        if (Stack.Count > 0) Stack.TryRemove(other, false);
    }

    /// <summary>A link option is UI navigation, not a letter decision: it must stay local and be recorded as residual.</summary>
    private static void NavigationPath()
    {
        var letter = new TestNavigationLetter { ID = 42 };
        Letters.LettersListForReading.Add(letter);
        letter.OpenLetter();
        var dialog = Stack.Count == 1 ? Stack[0] as Dialog_NodeTree : null;
        Check("nav:dialog-opened", dialog != null, $"windows={Stack.Count}");
        if (dialog == null) return;
        var link = RootOf(dialog).options[0];
        var sentBefore = SharedActions.LetterChoices.Count;

        ActivateMethod.Invoke(link, null);
        Check("nav:published-nothing", SharedActions.LetterChoices.Count == sentBefore,
            $"sentDelta={SharedActions.LetterChoices.Count - sentBefore}");
        Check("nav:local-navigation-ran", ReferenceEquals(RootOf(dialog), letter.SubNode),
            $"current={(ReferenceEquals(RootOf(dialog), letter.SubNode) ? "sub" : "root")}");
        Check("nav:residual-recorded", LetterChoiceState.LastRefusal?.Contains("导航") == true,
            $"last={LetterChoiceState.LastRefusal ?? "-"}");
        if (Stack.Count > 0) Stack.TryRemove(dialog, false);
    }

    /// <summary>Postpone closes the dialog and keeps the letter; locally deleting the letter is the bug.</summary>
    private static void PostponePath()
    {
        var letter = new TestChoiceLetter { ID = 43 };
        Letters.LettersListForReading.Add(letter);
        letter.OpenLetter();
        var dialog = Stack.Count == 1 ? Stack[0] as Dialog_NodeTree : null;
        Check("postpone:dialog-opened", dialog != null, $"windows={Stack.Count}");
        if (dialog == null) return;
        var postpone = RootOf(dialog).options[0];
        var sentBefore = SharedActions.LetterChoices.Count;

        ActivateMethod.Invoke(postpone, null);
        Check("postpone:published-index-0",
            SharedActions.LetterChoices.Count == sentBefore + 1 && SharedActions.LetterChoices[^1] == (43, 0),
            $"sentDelta={SharedActions.LetterChoices.Count - sentBefore}");
        Check("postpone:dialog-closed", Stack.Count == 0, $"windows={Stack.Count}");
        Check("postpone:letter-not-deleted-locally", Letters.LettersListForReading.Contains(letter),
            $"letters={Letters.LettersListForReading.Count}");
    }

    /// <summary>The letters the game actually ships must reach the patched base method, not an override.</summary>
    private static void CoveragePath()
    {
        const BindingFlags declared = BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly;
        Check("coverage:StandardLetter-inherits-base-OpenLetter",
            typeof(StandardLetter).GetMethod(nameof(ChoiceLetter.OpenLetter), declared) == null,
            $"declaredOnStandardLetter={typeof(StandardLetter).GetMethod(nameof(ChoiceLetter.OpenLetter), declared) != null}");
        Check("coverage:test-subclass-inherits-base-OpenLetter",
            typeof(TestChoiceLetter).GetMethod(nameof(ChoiceLetter.OpenLetter), declared) == null,
            $"declaredOnTestLetter={typeof(TestChoiceLetter).GetMethod(nameof(ChoiceLetter.OpenLetter), declared) != null}");
        Check("coverage:Activate-is-nonpublic-hook",
            ActivateMethod is { IsPublic: false },
            $"isPublic={ActivateMethod.IsPublic}");
    }

    private static DiaNode RootOf(Dialog_NodeTree dialog) => (DiaNode)CurNodeField.GetValue(dialog)!;

    private static string VersionFromGame()
    {
        try
        {
            var file = Path.Combine(GameDir, "Version.txt");
            return File.Exists(file) ? File.ReadAllText(file).Trim() : "unknown";
        }
        catch { return "unknown"; }
    }

    private static void Check(string name, bool ok, string detail)
    {
        if (ok) Passed++; else Failed++;
        Console.WriteLine($"CHECK {(ok ? "PASS" : "FAIL")} {name} {detail}");
    }

    private static bool CaptureLog(string text) { GameLogs.Add(text); return false; }

    private static bool WindowStackGetter(ref WindowStack __result) { __result = Stack; return false; }

    private static bool LetterStackGetter(ref LetterStack __result) { __result = Letters; return false; }
}

/// <summary>Fresh option objects per enumeration, like every real <c>ChoiceLetter.Choices</c> iterator.</summary>
internal sealed class TestChoiceLetter : ChoiceLetter
{
    internal int AcceptRuns;

    public override IEnumerable<DiaOption> Choices
    {
        get
        {
            yield return new DiaOption("Postpone") { resolveTree = true };
            yield return new DiaOption("Accept") { resolveTree = true, action = () => AcceptRuns++ };
        }
    }
}

internal sealed class TestNavigationLetter : ChoiceLetter
{
    internal readonly DiaNode SubNode = new("sub node");

    public override IEnumerable<DiaOption> Choices
    {
        get { yield return new DiaOption("More") { link = SubNode }; }
    }
}
