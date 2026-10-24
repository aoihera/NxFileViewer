using System.Collections.Generic;

namespace Emignatik.NxFileViewer.FileLoading.Patching;

/// <summary>
/// Locates the base package of an update package (the package holding the content that patch/BKTR sections rely on)
/// by searching the directory of the update package.
/// </summary>
public interface IBasePackageFinder
{
    /// <summary>
    /// Gets the packages of the same directory as <paramref name="packageFilePath"/> which may be the base package of
    /// the update, ordered by relevance:
    /// 1. packages referencing one of the wanted base title ids, lowest version first (so the «v0» is preferred),
    /// 2. then the remaining packages (a package whose metadata can't be read may still hold the right NCA).
    /// </summary>
    /// <param name="packageFilePath">Path of the package being opened (never returned)</param>
    /// <param name="baseTitleIds">Title ids of the base content, without the update (patch) bit</param>
    /// <returns>The candidate package paths, most relevant first (empty when there is no candidate)</returns>
    IReadOnlyList<string> FindBasePackageCandidates(string packageFilePath, IReadOnlyCollection<ulong> baseTitleIds);
}
