using System.Runtime.InteropServices;
using System.Text.Json;
using Haptics.Core;
using Haptics.Lab.Experiments;
using Haptics.Windows;

namespace Haptics.Lab;

internal static class Program
{
    private const string Usage = """
        haptics-lab — Haptics Everywhere showcase + lab (see README.md)

          haptics-lab                              open the Showcase
          haptics-lab coop-send <Waveform>         send Align/Success/Error/... as a cooperating app would
          haptics-lab doctor [--json]              EXP-000 capability inventory
          haptics-lab list-experiments
          haptics-lab run <EXP-ID> [options]       run one experiment (writes artifacts/runs/<run-id>/)
          haptics-lab run-all --eligible-only      run every automated experiment (never moves the pointer or buzzes)
          haptics-lab replay <trace.jsonl> [--profile Quiet|Balanced|Experimental]
          haptics-lab report                       write artifacts/report.md + report.html
          haptics-lab evidence summarize <run-dir> print a ledger entry for docs/EVIDENCE_LEDGER.md
          haptics-lab fixture [--truth <file>]     open the controlled UI fixture

        run options:
          --out <dir>               artifact directory (default: artifacts/runs/<run-id>)
          --trials-per-arm <n>      physical experiments (default 6)
          --controls <n>            no-pulse control trials (default 6)
          --seed <n>                trial order seed (default: random, recorded)
          --waveform <name>         EXP-020 waveform (default Click)
          --intensity <0..1>        default 1.0 for physical discrimination
          --window message|hidden   EXP-020 raw-input window kind (default message)
          --target mouse|touchpad|any   which haptic device the operator is using (default mouse)
          --seconds <n>             EXP-030 capture duration (default 20)
          --require-pass            exit nonzero for any result other than PASS (CI opt-in)
        """;

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == UiaWorker.Arg)
        {
            return UiaWorker.Run(args);
        }

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        if (args.Length == 0)
        {
            FreeConsole();
            RunShowcase();
            return 0;
        }

        var cmd = args[0].ToLowerInvariant();
        var opt = CliOptions.Parse(args.Skip(1));
        try
        {
            return cmd switch
            {
                "doctor" => Doctor(opt),
                "list-experiments" => ListExperiments(),
                "run" => RunExperiment(opt.Positional.FirstOrDefault() ?? throw new ArgumentException("run <EXP-ID>"), opt),
                "run-all" => RunAll(opt),
                "replay" => Replay(opt),
                "report" => WriteReport(),
                "evidence" => Evidence(opt),
                "fixture" => Fixture(opt),
                "dashboard" or "showcase" => RunDashboard(),
                "coop-send" => CoopSend(opt),
                "help" or "--help" or "-h" or "/?" => Help(),
                _ => Help(1),
            };
        }
        catch (Exception ex) when (ex is ArgumentException or FileNotFoundException or InvalidDataException)
        {
            Console.Error.WriteLine(ex.Message);
            return 2;
        }
    }

    private static int Help(int code = 0)
    {
        Console.WriteLine(Usage);
        return code;
    }

    private static int RunDashboard()
    {
        RunShowcase();
        return 0;
    }

    private static string? RunShowcase(bool startCompare = false)
    {
        // Create the real application before WPF-UI accesses its resources. The WinForms loop also pumps WPF
        // and keeps the existing Lab/fixture surfaces and physical-rating panels on their original UI thread.
        var wpf = new System.Windows.Application { ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown };
        wpf.Resources.MergedDictionaries.Add(new Wpf.Ui.Markup.ThemesDictionary { Theme = Wpf.Ui.Appearance.ApplicationTheme.Light });
        wpf.Resources.MergedDictionaries.Add(new Wpf.Ui.Markup.ControlsDictionary());
        Wpf.Ui.Appearance.ApplicationThemeManager.ApplySystemTheme();
        // Recoverable Showcase operations report errors after rolling back their own state. Do not blanket-handle
        // DispatcherUnhandledException: an unexpected handler bug must not leave the engine running as if it succeeded.
        using var context = new ApplicationContext();
        using var window = new ShowcaseWindow(startCompare);
        window.Closed += (_, _) => context.ExitThread();
        System.Windows.Forms.Integration.ElementHost.EnableModelessKeyboardInterop(window);
        Wpf.Ui.Appearance.SystemThemeWatcher.Watch(window);
        window.Show();
        Application.Run(context);
        wpf.Shutdown();
        return window.ResultPath;
    }

    private static int CoopSend(CliOptions opt)
    {
        var waveform = opt.Positional.FirstOrDefault() ?? throw new ArgumentException("coop-send <Waveform>");
        var ok = CooperativeServer.Send(waveform);
        Console.WriteLine(ok ? $"sent {waveform}" : "no Haptics Everywhere engine is listening (start the tray or the Showcase)");
        return ok ? 0 : 1;
    }

    private static int Doctor(CliOptions opt)
    {
        var path = Exp000Doctor.Run(opt.Get("out"));
        var result = JsonSerializer.Deserialize<ExperimentResult>(File.ReadAllText(path), HapticsJson.Indented)!;
        if (opt.Flag("json"))
        {
            Console.WriteLine(File.ReadAllText(path));
        }
        else
        {
            Console.WriteLine(Report.RunSummaryMarkdown(result));
            Console.WriteLine($"result: {path}");
        }

        return ExperimentExitCode(result.Status);
    }

    private static int ListExperiments()
    {
        var results = EvidenceLedger.Load();
        var fp = EnvironmentProbe.CurrentFingerprint();
        foreach (var e in ExperimentCatalog.All)
        {
            var latest = EvidenceLedger.Latest(results, e.Id, fp)?.Result.Status.ToString() ?? "NOT_RUN";
            Console.WriteLine($"{e.Id,-8} {(e.Implemented ? "impl" : "todo"),-5} {e.Mode,-9} {latest,-14} {e.Title}");
        }

        return 0;
    }

    internal static int RunExperiment(string id, CliOptions opt)
    {
        id = id.ToUpperInvariant();
        var outDir = opt.Get("out");
        string? path = id switch
        {
            "EXP-000" => Exp000Doctor.Run(outDir),
            "EXP-010" => RunForm(() => new Exp010ForegroundForm(opt)),
            "EXP-020" or "EXP-022" => RunForm(() => new Exp020BackgroundForm(opt)),
            "EXP-023" => Exp023ThreadAffinity.Run(outDir),
            "EXP-030" => RunForm(() => new Exp030RawInputForm(opt)),
            "EXP-040" => Exp040UiaFixture.Run(outDir),
            "EXP-043" => Exp043UiaContainment.Run(outDir),
            "EXP-070" => Exp070Replay.Run(outDir, opt.Get("trace")),
            "EXP-081" => Exp081StaticScan.Run(outDir),
            "EXP-090" => RunShowcase(startCompare: true),
            _ => null,
        };

        if (path is null)
        {
            var info = ExperimentCatalog.Find(id);
            Console.Error.WriteLine(info is null ? $"Unknown experiment {id}." : $"{id} is not implemented yet: {info.Summary}");
            return 3;
        }

        if (!File.Exists(path))
        {
            Console.WriteLine($"{id}: no result written (cancelled).");
            return 4;
        }

        var result = JsonSerializer.Deserialize<ExperimentResult>(File.ReadAllText(path), HapticsJson.Indented)!;
        Console.WriteLine($"{id}: {result.Status} [{result.EvidenceClass}] {result.Explanation}");
        Console.WriteLine($"result: {path}");
        return ExperimentExitCode(result.Status, opt.Flag("require-pass"));
    }

    internal static int ExperimentExitCode(ExperimentStatus status, bool requirePass = false) =>
        status == ExperimentStatus.FAIL || (requirePass && status != ExperimentStatus.PASS) ? 1 : 0;

    private static string? RunForm(Func<Form> create)
    {
        using var form = create();
        Application.Run(form);
        return (form as IExperimentForm)?.ResultPath;
    }

    private static int RunAll(CliOptions opt)
    {
        var worst = 0;
        foreach (var id in new[] { "EXP-000", "EXP-081", "EXP-023", "EXP-070", "EXP-043", "EXP-040" })
        {
            var code = RunExperiment(id, CliOptions.Parse([]));
            worst = Math.Max(worst, code);
        }

        Console.WriteLine("Physical experiments (EXP-010, EXP-020, EXP-030) need a human: run them from the dashboard.");
        return worst;
    }

    private static int Replay(CliOptions opt)
    {
        var trace = opt.Positional.FirstOrDefault() ?? throw new ArgumentException("replay <trace.jsonl>");
        var cfg = new PolicyConfig { Mode = HapticsMode.On, AppPolicy = AppPolicy.AllExceptExclusions };
        if (Enum.TryParse<EffectProfile>(opt.Get("profile"), true, out var profile))
        {
            cfg.ApplyProfile(profile);
        }

        var output = TraceReplay.Run(trace, cfg);
        foreach (var d in output.Decisions)
        {
            Console.WriteLine($"{d.T,8:0} {d.EventKind,-20} {d.ControlType,-12} {d.Outcome,-10} {d.Reason,-34} {d.Waveform}");
        }

        Console.WriteLine($"emitted={output.Emitted} decisions={output.Decisions.Count} (replay proves logic only; no hardware)");
        return 0;
    }

    private static int WriteReport()
    {
        var (md, html) = Report.Write();
        Console.WriteLine($"report: {md}");
        Console.WriteLine($"report: {html}");
        return 0;
    }

    private static int Evidence(CliOptions opt)
    {
        if (opt.Positional.FirstOrDefault() != "summarize" || opt.Positional.Count < 2)
        {
            throw new ArgumentException("evidence summarize <run-dir>");
        }

        var dir = opt.Positional[1];
        var file = Directory.Exists(dir) ? Path.Combine(dir, "result.json") : dir;
        var r = JsonSerializer.Deserialize<ExperimentResult>(File.ReadAllText(file), HapticsJson.Indented)!;
        Console.WriteLine(Report.LedgerEntry(r));
        return 0;
    }

    private static int Fixture(CliOptions opt)
    {
        Application.Run(new FixtureForm(opt.Get("truth")));
        return 0;
    }

    [DllImport("kernel32.dll")]
    private static extern bool FreeConsole();
}

/// <summary>Interactive experiment windows expose the result they wrote (null if cancelled).</summary>
public interface IExperimentForm
{
    string? ResultPath { get; }
}
