using System;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using Crimson.Models;

namespace Crimson.Core;

public static class OperationDiagnostics
{
    private const string IssueUrl = "https://github.com/Foxtrot47/Crimson/issues/new";

    public static string Create(OperationSnapshot operation)
    {
        var version = Assembly.GetEntryAssembly()?.GetName().Version?.ToString()
            ?? typeof(OperationDiagnostics).Assembly.GetName().Version?.ToString()
            ?? "unknown";
        var diagnostic = new StringBuilder()
            .AppendLine("Crimson operation diagnostic")
            .AppendLine($"Version: {version}")
            .AppendLine($"Operating system: {RuntimeInformation.OSDescription}")
            .AppendLine($"Action: {operation.Action}")
            .AppendLine($"Status: {operation.Status}")
            .AppendLine($"Phase: {operation.Phase}");

        if (operation.Failure is { } failure)
            diagnostic.AppendLine($"Failure code: {failure.Code}");

        return diagnostic.ToString().TrimEnd();
    }

    public static Uri CreateIssueUri(OperationSnapshot operation)
    {
        var prefix = operation.Status == ActionStatus.Failed ? "Operation failed" : "Operation error";
        var title = Uri.EscapeDataString($"{prefix}: {operation.Action}");
        var body = Uri.EscapeDataString(
            "Describe what happened and what you expected.\n\n" +
            "Sanitized diagnostic:\n```text\n" + Create(operation) + "\n```");
        return new Uri($"{IssueUrl}?title={title}&body={body}");
    }
}
