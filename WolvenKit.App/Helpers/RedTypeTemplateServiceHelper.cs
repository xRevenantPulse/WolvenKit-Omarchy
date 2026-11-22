using System;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using WolvenKit.Common;
using WolvenKit.Common.Model;
using WolvenKit.Common.Services;
using WolvenKit.Core.Interfaces;
using WolvenKit.RED4.Types;

namespace WolvenKit.App.Helpers;

public static class RedTypeTemplateServiceHelper
{
    private static readonly HttpClient s_httpClient = ProtonHttpClientFactory.CreateHttpClient();
    private static readonly SemaphoreSlim s_updateLock = new(1, 1);

    /// <summary>
    /// This is a helper method to correctly handle the Raw option when using RedTypeTemplateSelectionOption
    /// </summary>
    /// <param name="templateService"></param>
    /// <param name="templateOption"></param>
    /// <param name="fallbackType">type to be used for the Raw option</param>
    /// <returns></returns>
    public static IRedType CreateTypeInstanceFromSelectionOption(this RedTypeTemplateService templateService,
        RedTypeTemplateSelectionOption templateOption, Type fallbackType)
        => templateOption.Source switch
        {
            RedTypeTemplateSelectionOptionSource.Raw => RedTypeManager.CreateAndInitRedType(fallbackType),
            RedTypeTemplateSelectionOptionSource.User => templateService.CreateTypeInstance(templateOption, TemplateSource.User),
            RedTypeTemplateSelectionOptionSource.System => templateService.CreateTypeInstance(templateOption, TemplateSource.System),
            _ => throw new ArgumentOutOfRangeException(nameof(templateOption), templateOption.Source.ToString())
        };

    /// <summary>
    /// Updates the system templates from the remote WolvenKit/WolvenKit-Resources repository.
    /// </summary>
    /// <param name="loggerService"></param>
    /// <returns>true if templates are up to date or have been updated, false if status could not be verified or failed to download updated templates</returns>
    /// <remarks>Does not trigger the template service to load them from disk</remarks>
    public static async Task<bool> UpdateSystemTemplatesFromRemote(ILoggerService? loggerService = null)
    {
        return await UpdateSystemTemplatesFromRemoteInternal(loggerService);
    }

    private static async Task<bool> UpdateSystemTemplatesFromRemoteInternal(ILoggerService? loggerService)
    {
        try
        {
            await TransactionalContentUpdater.UpdateAsync(s_httpClient, AppDomain.CurrentDomain.BaseDirectory,
                "Templates", "templatehash.txt",
                new Uri("https://wolvenkit.github.io/Wolvenkit-Resources/templatehash.txt"),
                new Uri("https://wolvenkit.github.io/Wolvenkit-Resources/templates.zip"),
                s_updateLock, cleanupWarning: ex => loggerService?.Warning(ex.ToString()));
            return true;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException or InvalidDataException or UnauthorizedAccessException)
        {
            loggerService?.Warning("Template resource update failed; previous cache retained unless rollback recovery is reported.");
            loggerService?.Error(ex);
            return false;
        }
        catch (Exception ex)
        {
            loggerService?.Error(ex);
            throw;
        }
    }

}
