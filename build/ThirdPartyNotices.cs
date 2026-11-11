// Third-party notices (task 67): the desktop app's build runs GenerateThirdPartyNotices as an inline MSBuild task
// (RoslynCodeTaskFactory, see src/AiLocalPack.Desktop/AiLocalPack.Desktop.csproj) over the NuGet packages that ship in
// the app, reading each package's .nuspec; the result is embedded in the app (Settings → About → Third-party notices).
// The writer below is plain .NET so Desktop.Tests compile this file too (with NOTICES_WRITER_ONLY: no MSBuild types).
// Kept to C# that the inline task compiler takes: no records, no init-only setters.
#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml.Linq;
#if !NOTICES_WRITER_ONLY
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;
#endif

namespace AiLocalPack.Build
{
#if !NOTICES_WRITER_ONLY
    /// <summary>Writes the third-party notices of the packages given (and the extra components) to <see cref="OutputFile"/>.</summary>
    public sealed class GenerateThirdPartyNotices : Microsoft.Build.Utilities.Task
    {
        /// <summary>Packages: item spec = package ID, metadata <c>NuGetPackageVersion</c> (as ResolvePackageAssets gives them).</summary>
        [Required]
        public ITaskItem[] Packages { get; set; } = Array.Empty<ITaskItem>();

        /// <summary>Components that aren't packages (bundled fonts, the .NET runtime): metadata Version, License, LicenseFile, Copyright, ProjectUrl.</summary>
        public ITaskItem[] Extra { get; set; } = Array.Empty<ITaskItem>();

        /// <summary>The license of a package whose .nuspec names only a URL: item spec = package ID, metadata License (an SPDX ID).</summary>
        public ITaskItem[] Overrides { get; set; } = Array.Empty<ITaskItem>();

        [Required]
        public string PackageRoot { get; set; } = string.Empty;

        /// <summary>A folder of license texts named by SPDX ID (<c>MIT.txt</c>).</summary>
        [Required]
        public string LicensesFolder { get; set; } = string.Empty;

        [Required]
        public string OutputFile { get; set; } = string.Empty;

        public override bool Execute()
        {
            var overrides = Overrides.ToDictionary(o => o.ItemSpec, o => o.GetMetadata("License"), StringComparer.OrdinalIgnoreCase);
            var components = new List<NoticeComponent>();
            foreach (var package in Packages
                         .Select(p => new { Id = p.ItemSpec, Version = p.GetMetadata("NuGetPackageVersion") })
                         .Distinct())
            {
                var folder = Path.Combine(PackageRoot, package.Id.ToLowerInvariant(), package.Version.ToLowerInvariant());
                var nuspec = Path.Combine(folder, package.Id.ToLowerInvariant() + ".nuspec");
                if (!File.Exists(nuspec))
                {
                    Log.LogError("Third-party notices: no .nuspec for {0} {1} at {2}", package.Id, package.Version, nuspec);
                    continue;
                }

                overrides.TryGetValue(package.Id, out var license);
                components.Add(ThirdPartyNotices.FromNuspec(
                    File.ReadAllText(nuspec),
                    file => ReadInside(folder, file),
                    string.IsNullOrEmpty(license) ? null : license));
            }

            foreach (var extra in Extra)
            {
                var licenseFile = extra.GetMetadata("LicenseFile");
                components.Add(new NoticeComponent(
                    extra.ItemSpec,
                    extra.GetMetadata("Version"),
                    NullIfEmpty(extra.GetMetadata("License")),
                    string.IsNullOrEmpty(licenseFile) ? null : File.ReadAllText(licenseFile),
                    null,
                    NullIfEmpty(extra.GetMetadata("ProjectUrl")),
                    NullIfEmpty(extra.GetMetadata("Copyright"))));
            }

            var texts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in Directory.GetFiles(LicensesFolder, "*.txt"))
            {
                texts[Path.GetFileNameWithoutExtension(file)] = File.ReadAllText(file);
            }

            var result = ThirdPartyNotices.Render(components, texts);
            foreach (var problem in result.Problems)
            {
                Log.LogError("Third-party notices: {0}", problem);
            }

            if (Log.HasLoggedErrors)
            {
                return false;
            }

            // Written only when it changed, so an unchanged build stays incremental.
            if (!File.Exists(OutputFile) || File.ReadAllText(OutputFile) != result.Text)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(OutputFile))!);
                File.WriteAllText(OutputFile, result.Text, new UTF8Encoding(false));
            }

            return true;
        }

        private static string? NullIfEmpty(string value) => string.IsNullOrEmpty(value) ? null : value;

        /// <summary>A file of the package (its license file), never outside the package folder.</summary>
        private static string? ReadInside(string folder, string relative)
        {
            var root = Path.GetFullPath(folder) + Path.DirectorySeparatorChar;
            var path = Path.GetFullPath(Path.Combine(folder, relative));
            return path.StartsWith(root, StringComparison.Ordinal) && File.Exists(path) ? File.ReadAllText(path) : null;
        }
    }
#endif

    /// <summary>One third-party component of the app and what its notice needs.</summary>
    public sealed class NoticeComponent
    {
        public NoticeComponent(string name, string version, string? license, string? licenseText, string? licenseUrl, string? projectUrl, string? copyright)
        {
            Name = name;
            Version = version;
            License = license;
            LicenseText = licenseText;
            LicenseUrl = licenseUrl;
            ProjectUrl = projectUrl;
            Copyright = copyright;
        }

        public string Name { get; }

        public string Version { get; }

        /// <summary>An SPDX license expression (<c>MIT</c>, <c>MIT OR Apache-2.0</c>); <c>null</c> when the component brings its own text.</summary>
        public string? License { get; }

        /// <summary>The component's own license text (a .nuspec's <c>&lt;license type="file"&gt;</c>).</summary>
        public string? LicenseText { get; }

        /// <summary>A license given only as an address (old .nuspec files); needs an override with its SPDX ID.</summary>
        public string? LicenseUrl { get; }

        public string? ProjectUrl { get; }

        /// <summary>The copyright line (the authors when the package names no copyright).</summary>
        public string? Copyright { get; }
    }

    /// <summary>The notices text and what keeps it from being complete (a license without a text, a component without a license).</summary>
    public sealed class NoticesResult
    {
        public NoticesResult(string text, IReadOnlyList<string> problems)
        {
            Text = text;
            Problems = problems;
        }

        public string Text { get; }

        public IReadOnlyList<string> Problems { get; }
    }

    /// <summary>Reads .nuspec license data and renders the notices: one entry per component by name, then each license text once.</summary>
    public static class ThirdPartyNotices
    {
        private const string Rule = "----------------------------------------------------------------------";
        private const string DoubleRule = "======================================================================";

        /// <summary>
        /// The component a .nuspec describes. <paramref name="readFile"/> reads a file of the package (a license file);
        /// <paramref name="licenseOverride"/> replaces the package's license (an SPDX expression a maintainer checked).
        /// </summary>
        public static NoticeComponent FromNuspec(string nuspecXml, Func<string, string?> readFile, string? licenseOverride = null)
        {
            var metadata = XDocument.Parse(nuspecXml).Root?.Elements().FirstOrDefault(e => e.Name.LocalName == "metadata")
                ?? throw new FormatException("The .nuspec has no <metadata>.");
            string? Value(string name)
            {
                var value = metadata.Elements().FirstOrDefault(e => e.Name.LocalName == name)?.Value.Trim();
                return string.IsNullOrEmpty(value) ? null : value;
            }

            var licenseElement = metadata.Elements().FirstOrDefault(e => e.Name.LocalName == "license");
            var licenseType = licenseElement?.Attribute("type")?.Value;
            string? expression = null;
            string? text = null;
            string? url = null;
            if (licenseOverride is not null)
            {
                expression = licenseOverride;
            }
            else if (licenseType == "expression")
            {
                expression = licenseElement!.Value.Trim();
            }
            else if (licenseType == "file")
            {
                text = readFile(licenseElement!.Value.Trim());
            }
            else
            {
                url = Value("licenseUrl");
            }

            return new NoticeComponent(
                Value("id") ?? "?",
                Value("version") ?? "?",
                expression,
                text,
                url,
                Value("projectUrl"),
                Value("copyright") ?? (Value("authors") is { } authors ? "Authors: " + authors : null));
        }

        /// <summary>The license IDs of an SPDX expression (<c>(MIT OR Apache-2.0) WITH x</c> → MIT, Apache-2.0).</summary>
        public static IReadOnlyList<string> LicenseIds(string expression)
        {
            var parts = expression.Replace("(", " ").Replace(")", " ").Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            var ids = new List<string>();
            for (var i = 0; i < parts.Length; i++)
            {
                var part = parts[i];
                if (part is "OR" or "AND")
                {
                    continue;
                }

                if (part == "WITH")
                {
                    i++;
                    continue;
                }

                if (!ids.Contains(part, StringComparer.OrdinalIgnoreCase))
                {
                    ids.Add(part);
                }
            }

            return ids;
        }

        /// <summary>
        /// The notices: a header, the components by name (license, copyright, project), then the component's own license
        /// texts and each license used once. Line ends are <c>\n</c>, so the text is the same on every OS.
        /// </summary>
        public static NoticesResult Render(IEnumerable<NoticeComponent> components, IReadOnlyDictionary<string, string> licenseTexts)
        {
            var list = components
                .GroupBy(c => c.Name + "\n" + c.Version, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(c => c.Version, StringComparer.Ordinal)
                .ToList();
            var problems = new List<string>();
            var used = new List<string>();
            var text = new StringBuilder();
            text.Append("AI Local Pack: third-party notices\n\n");
            text.Append("AI Local Pack is licensed under the Apache License 2.0. It includes the third-party components below,\n");
            text.Append("each under its own license. The license texts follow the list.\n");
            foreach (var component in list)
            {
                text.Append('\n').Append(Rule).Append('\n');
                text.Append(component.Name).Append(' ').Append(component.Version).Append('\n');
                if (component.License is { } license)
                {
                    text.Append("License: ").Append(license).Append('\n');
                    foreach (var id in LicenseIds(license))
                    {
                        if (!licenseTexts.ContainsKey(id))
                        {
                            problems.Add(string.Format(CultureInfo.InvariantCulture, "no text for the license {0} of {1} {2}: add build/licenses/{0}.txt", id, component.Name, component.Version));
                        }
                        else if (!used.Contains(id, StringComparer.OrdinalIgnoreCase))
                        {
                            used.Add(id);
                        }
                    }
                }
                else if (component.LicenseText is not null)
                {
                    text.Append("License: its own, below\n");
                }
                else
                {
                    text.Append("License: ").Append(component.LicenseUrl ?? "unknown").Append('\n');
                    problems.Add(string.Format(
                        CultureInfo.InvariantCulture,
                        "{0} {1} names no license expression ({2}): check it and add a NoticeLicense override with its SPDX ID",
                        component.Name,
                        component.Version,
                        component.LicenseUrl ?? "no license at all"));
                }

                if (component.Copyright is { } copyright)
                {
                    text.Append(copyright).Append('\n');
                }

                if (component.ProjectUrl is { } project)
                {
                    text.Append(project).Append('\n');
                }
            }

            foreach (var component in list.Where(c => c.License is null && c.LicenseText is not null))
            {
                text.Append('\n').Append(DoubleRule).Append('\n');
                text.Append(component.Name).Append(' ').Append(component.Version).Append(": license\n\n");
                text.Append(Normalize(component.LicenseText!)).Append('\n');
            }

            foreach (var id in used.OrderBy(i => i, StringComparer.OrdinalIgnoreCase))
            {
                text.Append('\n').Append(DoubleRule).Append('\n');
                text.Append(id).Append("\n\n");
                text.Append(Normalize(licenseTexts[id])).Append('\n');
            }

            return new NoticesResult(text.ToString(), problems);
        }

        private static string Normalize(string text) => text.Replace("\r\n", "\n").Replace('\r', '\n').Trim('\n', ' ', '﻿');
    }
}
