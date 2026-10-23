using System;
using System.Threading.Tasks;
using Crimson.Models;
using Serilog;

namespace Crimson.Core;

internal static class OperationDiagnosticActions
{
    public static bool TryCopy(OperationSnapshot operation, Action<string> copy, ILogger log)
    {
        try
        {
            copy(OperationDiagnostics.Create(operation));
            return true;
        }
        catch (Exception exception)
        {
            log.Warning("Diagnostics could not be copied: {ErrorType}", exception.GetType().Name);
            return false;
        }
    }

    public static async Task<bool> TryReportAsync(OperationSnapshot operation, Func<Uri, Task<bool>> launch, ILogger log)
    {
        try
        {
            return await launch(OperationDiagnostics.CreateIssueUri(operation));
        }
        catch (Exception exception)
        {
            log.Warning("Report page could not be opened: {ErrorType}", exception.GetType().Name);
            return false;
        }
    }
}
