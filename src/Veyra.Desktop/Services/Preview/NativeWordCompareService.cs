using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Veyra.Desktop.Services.Preview;

public sealed class NativeWordCompareService(ILogger<NativeWordCompareService> log) : INativeWordCompareService
{
    private const int WdCompareDestinationNew = 2;
    private const int WdGranularityWordLevel = 1;

    public bool IsAvailable => OperatingSystem.IsWindows() && TryResolveWordApplicationType() is not null;

    public Task<NativeWordCompareLaunchResult> OpenCompareAsync(
        string leftFilePath,
        string rightFilePath,
        NativeWordCompareOptions? options = null,
        CancellationToken ct = default)
    {
        if (!OperatingSystem.IsWindows())
            return Task.FromResult(NativeWordCompareLaunchResult.Fail("Native Word compare is available only on Windows."));

        if (string.IsNullOrWhiteSpace(leftFilePath) || string.IsNullOrWhiteSpace(rightFilePath))
            return Task.FromResult(NativeWordCompareLaunchResult.Fail("Both version files must be prepared before opening Word compare."));

        if (!File.Exists(leftFilePath) || !File.Exists(rightFilePath))
            return Task.FromResult(NativeWordCompareLaunchResult.Fail("Prepared version files are missing on disk."));

        var wordType = TryResolveWordApplicationType();
        if (wordType is null)
            return Task.FromResult(NativeWordCompareLaunchResult.Fail("Microsoft Word is not installed or COM automation is unavailable."));

        var effectiveOptions = options ?? NativeWordCompareOptions.ContentOnly;
        if (string.IsNullOrWhiteSpace(effectiveOptions.RevisedAuthor))
            effectiveOptions = effectiveOptions with { RevisedAuthor = ResolveAuthor() };

        var tcs = new TaskCompletionSource<NativeWordCompareLaunchResult>(TaskCreationOptions.RunContinuationsAsynchronously);

        var thread = new Thread(() =>
        {
            try
            {
                var result = OpenCompareOnSta(wordType, leftFilePath, rightFilePath, effectiveOptions);
                tcs.TrySetResult(result);
            }
            catch (Exception ex)
            {
                log.LogError(ex,
                    "Failed to open native Word compare. Left {LeftPath}. Right {RightPath}",
                    leftFilePath,
                    rightFilePath);

                tcs.TrySetResult(NativeWordCompareLaunchResult.Fail(ex.Message));
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();

        if (!ct.CanBeCanceled)
            return tcs.Task;

        ct.Register(() => tcs.TrySetCanceled(ct));
        return tcs.Task;
    }

    [SupportedOSPlatform("windows")]
    private NativeWordCompareLaunchResult OpenCompareOnSta(
        Type wordType,
        string leftFilePath,
        string rightFilePath,
        NativeWordCompareOptions options)
    {
        object? appObj = null;
        object? documentsObj = null;
        object? leftDocObj = null;
        object? rightDocObj = null;
        object? comparedDocObj = null;

        try
        {
            appObj = Activator.CreateInstance(wordType);
            if (appObj is null)
                return NativeWordCompareLaunchResult.Fail("Unable to initialize Microsoft Word automation instance.");

            dynamic app = appObj;
            app.Visible = true;
            app.ScreenUpdating = false;
            app.DisplayAlerts = 0;

            documentsObj = app.Documents;
            dynamic documents = documentsObj!;

            leftDocObj = OpenReadOnlyDocument(documents, leftFilePath);
            rightDocObj = OpenReadOnlyDocument(documents, rightFilePath);

            dynamic leftDoc = leftDocObj!;
            dynamic rightDoc = rightDocObj!;

            comparedDocObj = app.CompareDocuments(
                leftDoc,
                rightDoc,
                WdCompareDestinationNew,
                WdGranularityWordLevel,
                options.CompareFormatting,
                options.CompareCaseChanges,
                options.CompareWhitespace,
                options.CompareTables,
                options.CompareHeaders,
                options.CompareFootnotes,
                options.CompareTextboxes,
                options.CompareFields,
                options.CompareComments,
                options.CompareMoves,
                options.RevisedAuthor,
                options.IgnoreAllComparisonWarnings);

            dynamic comparedDoc = comparedDocObj!;
            comparedDoc.Activate();

            app.ScreenUpdating = true;
            app.Visible = true;

            try
            {
                app.ActiveWindow.View.ShowRevisionsAndComments = true;
            }
            catch
            {
            }

            try
            {
                app.UserControl = true;
            }
            catch
            {
            }

            CloseWithoutSave(leftDocObj);
            CloseWithoutSave(rightDocObj);

            SafeReleaseComObject(leftDocObj);
            SafeReleaseComObject(rightDocObj);
            leftDocObj = null;
            rightDocObj = null;

            SafeReleaseComObject(comparedDocObj);
            comparedDocObj = null;

            SafeReleaseComObject(documentsObj);
            documentsObj = null;

            SafeReleaseComObject(appObj);
            appObj = null;

            return NativeWordCompareLaunchResult.Ok();
        }
        catch (Exception ex)
        {
            log.LogError(ex,
                "Native Word compare launch failed. Left {LeftPath}. Right {RightPath}",
                leftFilePath,
                rightFilePath);

            return NativeWordCompareLaunchResult.Fail($"Microsoft Word compare failed: {ex.Message}");
        }
        finally
        {
            CloseWithoutSave(leftDocObj);
            CloseWithoutSave(rightDocObj);

            SafeReleaseComObject(leftDocObj);
            SafeReleaseComObject(rightDocObj);
            SafeReleaseComObject(comparedDocObj);
            SafeReleaseComObject(documentsObj);
            SafeReleaseComObject(appObj);
        }
    }

    private static object? OpenReadOnlyDocument(dynamic documents, string path)
    {
        try
        {
            return documents.Open(
                FileName: path,
                ConfirmConversions: false,
                ReadOnly: true,
                AddToRecentFiles: false,
                Visible: false);
        }
        catch
        {
            return documents.Open(path);
        }
    }

    private static string ResolveAuthor()
    {
        var user = Environment.UserName;
        if (!string.IsNullOrWhiteSpace(user))
            return user;

        return "User";
    }

    private static void CloseWithoutSave(object? docObj)
    {
        if (docObj is null)
            return;

        try
        {
            dynamic doc = docObj;
            doc.Close(false);
        }
        catch
        {
        }
    }

    [SupportedOSPlatform("windows")]
    private static Type? TryResolveWordApplicationType()
    {
        try
        {
            return Type.GetTypeFromProgID("Word.Application", throwOnError: false);
        }
        catch
        {
            return null;
        }
    }

    [SupportedOSPlatform("windows")]
    private static void SafeReleaseComObject(object? comObj)
    {
        if (comObj is null)
            return;

        try
        {
            if (Marshal.IsComObject(comObj))
                Marshal.FinalReleaseComObject(comObj);
        }
        catch
        {
        }
    }
}
