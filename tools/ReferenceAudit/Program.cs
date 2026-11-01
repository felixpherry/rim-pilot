using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text.Json;
using System.Xml.Linq;

namespace RimPilot.ReferenceAudit;

// Read PE metadata only. Never load game assemblies or execute game code.
internal static class Program
{
    private sealed record Identity(string Name, Version Version, string Token);
    private sealed record AssemblyInfo(string Path, Identity Identity, Identity[] References, string Sha256);

    private static int Main(string[] args)
    {
        try
        {
            if (args.Length != 3)
                throw new InvalidOperationException("Usage: ReferenceAudit <RimWorld root> <Harmony mod root> <private output.props>");
            Audit(Path.GetFullPath(args[0]), Path.GetFullPath(args[1]), Path.GetFullPath(args[2]));
            return 0;
        }
        catch (Exception error) when (error is IOException or InvalidOperationException or BadImageFormatException or System.Xml.XmlException)
        {
            Console.Error.WriteLine($"Reference audit failed: {error.Message}");
            return 1;
        }
    }

    private static void Audit(string game, string harmony, string output)
    {
        var managed = Path.Combine(game, "RimWorldLinux_Data", "Managed");
        var about = XDocument.Load(Path.Combine(harmony, "About", "About.xml"));
        if (!string.Equals(about.Root?.Element("packageId")?.Value.Trim(), "brrainz.harmony", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Harmony package ID must be brrainz.harmony.");
        var versionFolders = XDocument.Load(Path.Combine(harmony, "LoadFolders.xml")).Root?.Element("v1.6")
            ?? throw new InvalidOperationException("Harmony has no explicit RimWorld 1.6 load-folder declaration.");
        var candidates = new List<string>();
        foreach (var entry in versionFolders.Elements())
        {
            if (entry.Name != "li" || entry.HasAttributes)
                throw new InvalidOperationException("Conditional/unknown Harmony load folders require an explicit audit.");
            var relative = entry.Value.Trim();
            var folder = relative == "/" ? harmony : Path.GetFullPath(Path.Combine(harmony, relative));
            if (folder != harmony && !folder.StartsWith(harmony + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                throw new InvalidOperationException("Harmony load folder escapes mod root.");
            var candidate = Path.Combine(folder, "Assemblies", "0Harmony.dll");
            if (File.Exists(candidate))
                candidates.Add(candidate);
        }
        if (candidates.Count != 1)
            throw new InvalidOperationException("Expected exactly one selected 0Harmony assembly; refuse ambiguous/absent dependency.");
        var harmonyAssembly = Read(candidates[0]);
        var gameAssembly = Read(Path.Combine(managed, "Assembly-CSharp.dll"));
        if (gameAssembly.Identity.Name != "Assembly-CSharp" || gameAssembly.Identity.Version.Major != 1 || gameAssembly.Identity.Version.Minor != 6)
            throw new InvalidOperationException("This foundation audits installed RimWorld 1.6 assemblies only.");
        var core = Read(Path.Combine(managed, "mscorlib.dll"));
        var standard = Read(Path.Combine(managed, "netstandard.dll"));
        RequireIdentity(core.Identity, "mscorlib", new Version(4, 0, 0, 0), "b77a5c561934e089");
        RequireIdentity(standard.Identity, "netstandard", new Version(2, 1, 0, 0), "cc7b13ffcd2ddd51");
        RequireIdentity(harmonyAssembly.Identity, "0Harmony", harmonyAssembly.Identity.Version, "");
        RequireReference(gameAssembly, core.Identity);
        RequireReference(gameAssembly, standard.Identity);
        RequireReference(harmonyAssembly, core.Identity);
        RequireReference(standard, core.Identity);

        var closure = new Dictionary<string, AssemblyInfo>(StringComparer.OrdinalIgnoreCase);
        var pending = new Queue<AssemblyInfo>();
        foreach (var name in new[] { "Assembly-CSharp", "mscorlib", "netstandard", "System", "System.Core", "UnityEngine", "UnityEngine.CoreModule", "UnityEngine.IMGUIModule", "UnityEngine.ScreenCaptureModule", "UnityEngine.ImageConversionModule" })
            pending.Enqueue(Read(Path.Combine(managed, name + ".dll")));
        pending.Enqueue(harmonyAssembly);
        var unifications = new SortedSet<string>(StringComparer.Ordinal);
        while (pending.TryDequeue(out var assembly))
        {
            if (closure.TryGetValue(assembly.Identity.Name, out var existing))
            {
                if (existing.Path != assembly.Path)
                    throw new InvalidOperationException("Conflicting assembly paths in reference closure.");
                continue;
            }
            closure.Add(assembly.Identity.Name, assembly);
            foreach (var reference in assembly.References)
            {
                var target = reference.Name == "0Harmony" ? harmonyAssembly : Read(Path.Combine(managed, reference.Name + ".dll"));
                if (target.Identity.Name != reference.Name || target.Identity.Token != reference.Token || target.Identity.Version < reference.Version)
                    throw new InvalidOperationException($"Installed assembly cannot satisfy {assembly.Identity.Name} -> {reference}.");
                if (target.Identity.Version != reference.Version)
                    unifications.Add($"{assembly.Identity.Name}: {reference.Name} {reference.Version} -> {target.Identity.Version}");
                pending.Enqueue(target);
            }
        }
        // Generate only after the entire transitive metadata closure is verified.
        var references = closure.Values.OrderBy(item => item.Identity.Name, StringComparer.Ordinal).ToArray();
        var props = new XDocument(new XElement("Project",
            new XElement("PropertyGroup",
                new XElement("FrameworkPathOverride", managed),
                new XElement("TargetFrameworkRootPath", managed),
                new XElement("BypassFrameworkInstallChecks", "true")),
            new XElement("ItemGroup", references.Select(item => new XElement("Reference",
                new XAttribute("Include", item.Identity.Name),
                new XElement("HintPath", item.Path),
                new XElement("Private", "false"),
                new XElement("RimPilotGameReference", "true"))))));
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        props.Save(output);
        File.WriteAllText(output + ".audit.json", JsonSerializer.Serialize(new
        {
            GameAssembly = gameAssembly.Identity,
            HarmonyPackageVersion = about.Root?.Element("modVersion")?.Value,
            HarmonyAssembly = harmonyAssembly.Identity,
            HarmonySelectedFolder = Path.GetRelativePath(harmony, Path.GetDirectoryName(Path.GetDirectoryName(harmonyAssembly.Path))!),
            References = references,
            FrameworkUnifications = unifications,
            Note = "Metadata closure and compile references only; not proof of Mono loading or game behavior."
        }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"Verified installed mscorlib {core.Identity.Version}, netstandard {standard.Identity.Version}; {references.Length} assemblies in metadata closure.");
        Console.WriteLine($"Selected Harmony folder {Path.GetRelativePath(harmony, Path.GetDirectoryName(Path.GetDirectoryName(harmonyAssembly.Path))!)}; actual 0Harmony identity {harmonyAssembly.Identity.Version}.");
        foreach (var item in unifications)
            Console.WriteLine($"Installed framework unification: {item}");
        Console.WriteLine("All generated references are non-copy-local. Runtime Mono proof remains separate.");
    }

    private static void RequireIdentity(Identity identity, string name, Version version, string token)
    {
        if (identity != new Identity(name, version, token))
            throw new InvalidOperationException($"Unexpected installed identity: {identity}.");
    }

    private static void RequireReference(AssemblyInfo source, Identity expected)
    {
        if (!source.References.Contains(expected))
            throw new InvalidOperationException($"{source.Identity.Name} lacks expected reference {expected}.");
    }

    private static AssemblyInfo Read(string path)
    {
        using var stream = File.OpenRead(path);
        using var pe = new PEReader(stream);
        var metadata = pe.GetMetadataReader();
        var definition = metadata.GetAssemblyDefinition();
        var identity = new Identity(metadata.GetString(definition.Name), definition.Version,
            Token(metadata.GetBlobBytes(definition.PublicKey), true));
        var references = metadata.AssemblyReferences.Select(handle =>
        {
            var reference = metadata.GetAssemblyReference(handle);
            return new Identity(metadata.GetString(reference.Name), reference.Version,
                Token(metadata.GetBlobBytes(reference.PublicKeyOrToken), (reference.Flags & AssemblyFlags.PublicKey) != 0));
        }).ToArray();
        stream.Position = 0;
        return new AssemblyInfo(path, identity, references, Convert.ToHexStringLower(SHA256.HashData(stream)));
    }

    private static string Token(byte[] key, bool fullKey)
    {
        if (key.Length == 0)
            return "";
        if (!fullKey)
            return Convert.ToHexStringLower(key);
        // Strong-name token format uses SHA-1 by definition; not a security checksum.
        var hash = SHA1.HashData(key);
        return Convert.ToHexStringLower(hash[^8..].Reverse().ToArray());
    }
}
