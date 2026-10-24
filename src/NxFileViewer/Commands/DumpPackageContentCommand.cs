using System;
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
        if (_partitionItem == null)
            return;

        var selectedDir = _promptService.PromptSelectDir(LocalizationManager.Instance.Current.Keys.DumpContent_SelectDirectoryTitle);
        if (selectedDir == null)
            return;

        try
        {
            var runnable = _serviceProvider.GetRequiredService<IDumpPackageContentRunnable>();
            runnable.Setup(_partitionItem, selectedDir);

            await _backgroundTaskRunnerService.RunAsync(runnable);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, LocalizationManager.Instance.Current.Keys.DumpContent_Error.SafeFormat(ex.Message));
        }
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
