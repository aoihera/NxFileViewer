using System;
using System.Linq;
using System.Windows.Input;
using Emignatik.NxFileViewer.Localization;
using Emignatik.NxFileViewer.Models.TreeItems.Impl;
using Emignatik.NxFileViewer.Services.BackgroundTask;
using Emignatik.NxFileViewer.Services.BackgroundTask.RunnableImpl;
using Emignatik.NxFileViewer.Services.Prompting;
using Emignatik.NxFileViewer.Utils.MVVM.Commands;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Emignatik.NxFileViewer.Commands;

/// <summary>
/// Dumps the whole content of the selected partition (NSP/NSZ or XCI/XCZ partition) into a directory
/// chosen by the user, in a nxdumptool-like layout (one directory per NCA content type, one directory
/// per NCA section such as «exefs» or «romfs»).
/// </summary>
public class DumpPackageContentCommand : CommandBase, IDumpPackageContentCommand
{
    private readonly IPromptService _promptService;
    private readonly IServiceProvider _serviceProvider;
    private readonly IMainBackgroundTaskRunnerService _backgroundTaskRunnerService;
    private readonly ILogger _logger;

    private PartitionFileSystemItemBase? _partitionItem;

    public DumpPackageContentCommand(IPromptService promptService, IServiceProvider serviceProvider, IMainBackgroundTaskRunnerService backgroundTaskRunnerService, ILoggerFactory loggerFactory)
    {
        _logger = (loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory))).CreateLogger(this.GetType());
        _promptService = promptService ?? throw new ArgumentNullException(nameof(promptService));
        _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
        _backgroundTaskRunnerService = backgroundTaskRunnerService ?? throw new ArgumentNullException(nameof(backgroundTaskRunnerService));
    }

    public PartitionFileSystemItemBase PartitionItem
    {
        set
        {
            _partitionItem = value;
            TriggerCanExecuteChanged();
        }
    }

    public override async void Execute(object? parameter)
    {
        var partitionItem = _partitionItem;
        if (partitionItem == null)
            return;

        var keys = LocalizationManager.Instance.Current.Keys;

        var selectedDir = _promptService.PromptSelectDir(keys.DumpContent_SelectDirectoryTitle);
        if (selectedDir == null)
            return;

        string? basePackageFilePath = null;
        if (HasUnresolvedPatchSections(partitionItem))
        {
            // Patch (BKTR) sections only hold the data modified by an update: the base package is needed to dump them.
            // The user can cancel the prompt to dump everything but those sections.
            basePackageFilePath = _promptService.PromptOpenFile(keys.DumpContent_SelectBasePackageTitle, keys.OpenFile_Filter);
        }

        try
        {
            var runnable = _serviceProvider.GetRequiredService<IDumpPackageContentRunnable>();
            runnable.Setup(partitionItem, selectedDir, basePackageFilePath);

            await _backgroundTaskRunnerService.RunAsync(runnable);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, keys.DumpContent_Error.SafeFormat(ex.Message));
        }
    }

    /// <summary>
    /// Tells if the specified partition holds patch (BKTR) sections which can't be read without the base package.
    /// </summary>
    private static bool HasUnresolvedPatchSections(PartitionFileSystemItemBase partitionItem)
    {
        return partitionItem.NcaChildItems
            .SelectMany(ncaItem => ncaItem.ChildItems)
            .Any(sectionItem => sectionItem.FileSystem == null && sectionItem.SectionType.HasFlag(SectionType.Patch));
    }

    public override bool CanExecute(object? parameter)
    {
        return _partitionItem is { NcaChildItems.Length: > 0 } && !_backgroundTaskRunnerService.IsRunning;
    }
}

public interface IDumpPackageContentCommand : ICommand
{
    PartitionFileSystemItemBase PartitionItem { set; }
}
