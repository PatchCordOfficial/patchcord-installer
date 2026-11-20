#nullable enable
using System.IO;


using System.Reflection;
using System.Windows;
using PatchcordInstaller;

class Smoke
{
    [STAThread]
    static void Main()
    {
        var app = new Application();
        var window = new MainWindow();
        window.Measure(new Size(1000, 620));
        window.Arrange(new Rect(0, 0, 1000, 620));
        window.UpdateLayout();
        typeof(MainWindow).GetMethod("RefreshClients", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, null);
        foreach (var tab in new[] { "Nav_Proc", "Nav_Set", "Nav_Upd", "Nav_Dash" })
        {
            var radio = (System.Windows.Controls.RadioButton)window.FindName(tab);
            radio.Measure(new Size(790, 570));
        }

        var find = typeof(MainWindow).GetMethod("FindLatestAppDir", BindingFlags.NonPublic | BindingFlags.Static)!;
        string root = Path.Combine(AppContext.BaseDirectory, Guid.NewGuid().ToString("N"));
        foreach (var version in new[] { "app-1.0.9999", "app-1.1.2", "app-2.0.0", "app-invalid" })
            Directory.CreateDirectory(Path.Combine(root, version, "resources"));
        string? Find() => find.Invoke(null, new object[] { root }) as string;
        void Add(string version, string name = "app.asar") => File.WriteAllText(Path.Combine(root, version, "resources", name), "fixture");
        Add("app-2.0.0", "_app.asar");
        if (Find() is not null) throw new Exception("Accepted a backup-only folder.");
        Add("app-1.0.9999");
        Add("app-invalid");
        if (Find() != Path.Combine(root, "app-1.0.9999")) throw new Exception("Failed fallback to the usable installation.");
        Add("app-1.1.2");
        if (Find() != Path.Combine(root, "app-1.1.2")) throw new Exception("Selected the wrong version.");
        var resources = Path.Combine(Find()!, "resources");
        var source = Path.Combine(root, "source.asar");
        File.WriteAllText(source, "patched");
        var patch = typeof(MainWindow).GetMethod("ApplyPatch", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var restore = typeof(MainWindow).GetMethod("DoRestoreVanilla", BindingFlags.NonPublic | BindingFlags.Instance)!;
        patch.Invoke(window, new object[] { resources, source });
        patch.Invoke(window, new object[] { resources, source });
        if (File.ReadAllText(Path.Combine(resources, "app.asar")) != "patched" ||
            File.ReadAllText(Path.Combine(resources, "_app.asar")) != "fixture")
            throw new Exception("Patch or repatch lost the expected archive.");
        restore.Invoke(window, new object[] { resources });
        if (File.ReadAllText(Path.Combine(resources, "app.asar")) != "fixture" ||
            File.Exists(Path.Combine(resources, "_app.asar")))
            throw new Exception("Restore failed.");
        Console.WriteLine("WPF layout, Discord discovery, patch, repatch, and restore checks passed.");
    }
}



