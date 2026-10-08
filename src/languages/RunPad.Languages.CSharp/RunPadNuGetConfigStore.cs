using System.Xml;
using System.Xml.Linq;
using NuGet.Configuration;
using RunPad.Contracts;

namespace RunPad.Languages.CSharp;

// RunPad's own NuGet.Config (ApplicationData/RunPad/NuGet.Config, next to settings.json). Package
// search reads it and every script build restores with it (RestoreConfigFile), so the sources the
// configurator shows are exactly the ones used — without touching the user's global NuGet.Config,
// Visual Studio or the dotnet CLI. Seeded once from the user's effective NuGet settings.
//
// Written by hand rather than via PackageSourceProvider.SavePackageSources: that API keeps existing
// entries in place and appends new ones, so reordering (Move Up/Down) wouldn't stick. Read back
// through NuGet's own Settings/PackageSourceProvider, which handles encrypted and clear-text
// passwords the same way restore does.
public sealed class RunPadNuGetConfigStore : INuGetSourceStore
{
    public const string FileName = "NuGet.Config";
    private const string NuGetOrgName = "nuget.org";
    private const string NuGetOrgUrl = "https://api.nuget.org/v3/index.json";

    private readonly string _directory;
    private readonly Func<ISettings?> _seedSettings;
    private readonly object _lock = new();

    public RunPadNuGetConfigStore()
        : this(DefaultDirectory, LoadUserSettings)
    {
    }

    // Tests pass a temp directory and their own seed instead of the user's NuGet settings.
    internal RunPadNuGetConfigStore(string directory, Func<ISettings?> seedSettings)
    {
        _directory = directory;
        _seedSettings = seedSettings;
    }

    public static string DefaultDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RunPad");

    // The default config's path if it already exists — for readers (IntelliSense) that must follow
    // the same settings as restore but must never create the file themselves.
    public static string? ExistingDefaultConfigFile
    {
        get
        {
            var path = Path.Combine(DefaultDirectory, FileName);
            return File.Exists(path) ? path : null;
        }
    }

    public event Action? Changed;

    public string ConfigFilePath
    {
        get
        {
            var path = Path.Combine(_directory, FileName);
            lock (_lock)
            {
                if (!File.Exists(path))
                    Seed(path);
            }
            return path;
        }
    }

    public IReadOnlyList<NuGetSource> Load()
    {
        _ = ConfigFilePath; // seeds the file on first use
        lock (_lock)
        {
            var settings = Settings.LoadSpecificSettings(_directory, FileName);
            return new PackageSourceProvider(settings).LoadPackageSources()
                .Select(ToNuGetSource)
                .ToList();
        }
    }

    public void Save(IReadOnlyList<NuGetSource> sources)
    {
        var path = ConfigFilePath;
        lock (_lock)
        {
            // Everything outside the sources (globalPackagesFolder etc.) is kept as is.
            XElement? config = null;
            try
            {
                config = XDocument.Load(path).Root?.Element("config");
            }
            catch (XmlException)
            {
                // A hand-broken file is rewritten from the configurator's state.
            }

            Write(path, sources, config);
        }
        Changed?.Invoke();
    }

    private static ISettings? LoadUserSettings()
    {
        try
        {
            return Settings.LoadDefaultSettings(root: null);
        }
        catch
        {
            return null;
        }
    }

    private void Seed(string path)
    {
        List<NuGetSource> sources = [];
        XElement? config = null;

        if (_seedSettings() is { } settings)
        {
            try
            {
                sources = new PackageSourceProvider(settings).LoadPackageSources().Select(ToNuGetSource).ToList();
                config = new XElement("config",
                    AddItem("globalPackagesFolder", SettingsUtility.GetGlobalPackagesFolder(settings)));
            }
            catch
            {
                sources = [];
            }
        }

        if (sources.Count == 0)
            sources = [new NuGetSource(NuGetOrgName, NuGetOrgUrl)];

        Directory.CreateDirectory(_directory);
        Write(path, sources, config);
    }

    private static NuGetSource ToNuGetSource(PackageSource source) => new(
        source.Name,
        source.Source,
        source.IsEnabled,
        source.Credentials?.Username,
        source.Credentials?.Password);

    private static void Write(string path, IReadOnlyList<NuGetSource> sources, XElement? config)
    {
        var root = new XElement("configuration");
        if (config is not null)
            root.Add(new XElement(config));

        // <clear/>: nothing from machine-wide configs leaks into RunPad's source list.
        root.Add(new XElement("packageSources",
            new XElement("clear"),
            sources.Select(s => AddItem(s.Name, s.Url))));

        var disabled = sources.Where(s => !s.IsEnabled).ToList();
        if (disabled.Count > 0)
            root.Add(new XElement("disabledPackageSources", disabled.Select(s => AddItem(s.Name, "true"))));

        var withCredentials = sources.Where(s => s.HasCredentials).ToList();
        if (withCredentials.Count > 0)
            root.Add(new XElement("packageSourceCredentials", withCredentials.Select(CredentialsElement)));

        var tempPath = path + ".tmp";
        new XDocument(new XDeclaration("1.0", "utf-8", null), root).Save(tempPath);
        File.Move(tempPath, path, overwrite: true);
    }

    private static XElement CredentialsElement(NuGetSource source)
    {
        var element = new XElement(XmlConvert.EncodeLocalName(source.Name), AddItem("Username", source.Username!));
        if (!string.IsNullOrEmpty(source.Password))
            element.Add(EncryptPassword(source.Password) is { } encrypted
                ? AddItem("Password", encrypted)
                : AddItem("ClearTextPassword", source.Password));
        return element;
    }

    // NuGet can only encrypt on Windows (DPAPI, per user); elsewhere it stores clear text itself too.
    private static string? EncryptPassword(string password)
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            return EncryptionUtility.EncryptString(password);
        }
        catch (NotSupportedException)
        {
            return null;
        }
    }

    private static XElement AddItem(string key, string value) =>
        new("add", new XAttribute("key", key), new XAttribute("value", value));
}
