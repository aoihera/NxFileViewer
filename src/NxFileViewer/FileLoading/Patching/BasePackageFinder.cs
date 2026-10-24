using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Emignatik.NxFileViewer.Localization;
using Emignatik.NxFileViewer.Services.KeysManagement;
using Microsoft.Extensions.Logging;

namespace Emignatik.NxFileViewer.FileLoading.Patching;

/// <summary>
/// <see cref="IBasePackageFinder"/> implementation: it probes every package file located in the same directory as the
/// update package to find those referencing the base title ids, and orders them so that the most plausible base
/// (same title id, lowest version — the «v0» — first) is tried first.
/// Probing results are cached (invalidated by the file size and last write time) because a directory can hold many
/// packages and probing all of them on every file opening would be slow.
/// </summary>
public class BasePackageFinder : IBasePackageFinder
{
    /// <summary>
    /// Maximum number of packages with unknown title ids (unreadable metadata) that are still proposed as candidates.
    /// </summary>
    private const int MAX_UNKNOWN_TITLE_ID_CANDIDATES = 20;

    private static readonly string[] PACKAGE_FILE_EXTENSIONS = [".nsp", ".nsz", ".xci", ".xcz"];

    private static readonly Regex VERSION_REGEX = new(@"\[[vV](\d+)\]", RegexOptions.Compiled);

    private readonly IPackageTypeAnalyzer _packageTypeAnalyzer;
    private readonly IKeySetProviderService _keySetProviderService;
    private readonly ILogger _logger;

    private readonly ConcurrentDictionary<string, ProbedPackage> _probedPackages = new(StringComparer.OrdinalIgnoreCase);

    public BasePackageFinder(IPackageTypeAnalyzer packageTypeAnalyzer, IKeySetProviderService keySetProviderService, ILoggerFactory loggerFactory)
    {
        _packageTypeAnalyzer = packageTypeAnalyzer ?? throw new ArgumentNullException(nameof(packageTypeAnalyzer));
        _keySetProviderService = keySetProviderService ?? throw new ArgumentNullException(nameof(keySetProviderService));
        _logger = (loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory))).CreateLogger(this.GetType());
    }

    public IReadOnlyList<string> FindBasePackageCandidates(string packageFilePath, IReadOnlyCollection<ulong> baseTitleIds)
    {
        var directoryPath = Path.GetDirectoryName(packageFilePath);
        if (string.IsNullOrEmpty(directoryPath) || !Directory.Exists(directoryPath))
            return [];

        List<string> otherPackageFilePaths;
        try
        {
            var fullPackageFilePath = Path.GetFullPath(packageFilePath);

            otherPackageFilePaths = Directory.EnumerateFiles(directoryPath)
                .Where(filePath => PACKAGE_FILE_EXTENSIONS.Contains(Path.GetExtension(filePath), StringComparer.OrdinalIgnoreCase))
                .Where(filePath => !string.Equals(Path.GetFullPath(filePath), fullPackageFilePath, StringComparison.OrdinalIgnoreCase))
                .OrderBy(filePath => Path.GetFileName(filePath), StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Unable to browse «{0}» while looking for the base package.", directoryPath);
            return [];
        }

        if (otherPackageFilePaths.Count == 0)
            return [];

        var matchingCandidates = new List<(string FilePath, ulong Version)>();
        var unknownTitleIdCandidates = new List<string>();

        foreach (var otherPackageFilePath in otherPackageFilePaths)
        {
            var probedPackage = ProbePackage(otherPackageFilePath);

            if (probedPackage == null || probedPackage.TitleIds.Count == 0)
            {
                unknownTitleIdCandidates.Add(otherPackageFilePath);
                _logger.LogDebug("Probed «{0}»: no readable title id, kept as a last resort candidate.", Path.GetFileName(otherPackageFilePath));
                continue;
            }

            var isMatch = baseTitleIds.Count > 0 && probedPackage.TitleIds.Overlaps(baseTitleIds);

            _logger.LogDebug("Probed «{0}»: title ids [{1}] => {2}.",
                Path.GetFileName(otherPackageFilePath),
                string.Join(", ", probedPackage.TitleIds.Select(titleId => titleId.ToString("X16"))),
                isMatch ? "base candidate" : "not a base candidate");

            if (isMatch)
                matchingCandidates.Add((otherPackageFilePath, GetVersionFromFileName(otherPackageFilePath)));
        }

        // Lowest version first: the base application (v0) is the most plausible base of an update
        var candidates = matchingCandidates
            .OrderBy(candidate => candidate.Version)
            .ThenBy(candidate => Path.GetFileName(candidate.FilePath), StringComparer.OrdinalIgnoreCase)
            .Select(candidate => candidate.FilePath)
            .ToList();

        if (candidates.Count > 0)
        {
            _logger.LogInformation(LocalizationManager.Instance.Current.Keys.BasePackage_Found
                .SafeFormat(Path.GetFileName(candidates[0]), Path.GetFileName(packageFilePath)));
        }
        else
        {
            _logger.LogInformation(LocalizationManager.Instance.Current.Keys.BasePackage_NotFound
                .SafeFormat(Path.GetFileName(packageFilePath)));
        }

        candidates.AddRange(unknownTitleIdCandidates.Take(MAX_UNKNOWN_TITLE_ID_CANDIDATES));

        return candidates;
    }

    /// <summary>
    /// Reads the version of a package from its file name («Game [0100ABCD1234567800][v65536].nsp» => 65536).
    /// </summary>
    private static ulong GetVersionFromFileName(string packageFilePath)
    {
        try
        {
            var match = VERSION_REGEX.Match(Path.GetFileNameWithoutExtension(packageFilePath));
            if (match.Success && ulong.TryParse(match.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var version))
                return version;
        }
        catch (Exception)
        {
            // Not a problem: the package simply won't be sorted by version
        }

        return ulong.MaxValue;
    }

    private ProbedPackage? ProbePackage(string packageFilePath)
    {
        try
        {
            var fileInfo = new FileInfo(packageFilePath);
            if (!fileInfo.Exists)
                return null;

            var lastWriteTimeUtc = fileInfo.LastWriteTimeUtc;
            var fileSize = fileInfo.Length;

            if (_probedPackages.TryGetValue(packageFilePath, out var probedPackage)
                && probedPackage.FileSize == fileSize
                && probedPackage.LastWriteTimeUtc == lastWriteTimeUtc)
            {
                return probedPackage;
            }

            var titleIds = ReadTitleIds(packageFilePath);

            probedPackage = new ProbedPackage(fileSize, lastWriteTimeUtc, titleIds);
            _probedPackages[packageFilePath] = probedPackage;

            return probedPackage;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Unable to read the title ids of «{0}».", packageFilePath);
            return null;
        }
    }

    private HashSet<ulong> ReadTitleIds(string packageFilePath)
    {
        var titleIds = new HashSet<ulong>();

        var packageType = _packageTypeAnalyzer.GetType(packageFilePath);
        if (packageType == PackageType.UNKNOWN)
            return titleIds;

        var keySet = _keySetProviderService.GetKeySet();

        using var packageContent = PackageContent.Open(packageFilePath, packageType, keySet);
        if (packageContent == null)
            return titleIds;

        foreach (var cnmt in packageContent.LoadCnmts())
        {
            if (cnmt.TitleId != 0)
                titleIds.Add(cnmt.TitleId & ~FileItemLoader.PATCH_TITLE_ID_MASK);
        }

        return titleIds;
    }

    private class ProbedPackage
    {
        public ProbedPackage(long fileSize, DateTime lastWriteTimeUtc, HashSet<ulong> titleIds)
        {
            FileSize = fileSize;
            LastWriteTimeUtc = lastWriteTimeUtc;
            TitleIds = titleIds;
        }

        public long FileSize { get; }

        public DateTime LastWriteTimeUtc { get; }

        public HashSet<ulong> TitleIds { get; }
    }
}
