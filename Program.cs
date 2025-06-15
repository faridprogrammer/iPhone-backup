using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.CommandLine;

namespace iPhoneBackup;

public class Program
{
    // --- CONFIGURATION ---
    private const string ProgressFileName = ".copy_progress.log";
    private const string ErrorFileName = ".copy_errors.log";

    /// <summary>
    /// Main entry point, configured with System.CommandLine.
    /// </summary>
    static async Task<int> Main(string[] args)
    {
        // --- DEFINE COMMAND-LINE ARGUMENTS ---
        // These arguments will be used by both the root command (copy) and the retry command.
        var sourceArgument = new Argument<DirectoryInfo>("source-dir", "The source directory to copy files from.")
        {
            Arity = ArgumentArity.ExactlyOne
        }.ExistingOnly(); // Automatically validates that the source directory exists.

        var destinationArgument = new Argument<DirectoryInfo>("dest-dir", "The destination directory where files will be copied.")
        {
            Arity = ArgumentArity.ExactlyOne
        };

        // --- DEFINE THE ROOT 'COPY' COMMAND ---
        var rootCommand = new RootCommand("A robust file copier that performs a full, resumable copy.");
        rootCommand.AddArgument(sourceArgument);
        rootCommand.AddArgument(destinationArgument);

        rootCommand.SetHandler((source, destination) =>
        {
            HandleCopyCommand(source, destination);
        }, sourceArgument, destinationArgument);

        // --- DEFINE THE 'RETRY' COMMAND ---
        var retryCommand = new Command("retry", "Retries copying only the files that failed in a previous run.");
        retryCommand.AddArgument(sourceArgument);
        retryCommand.AddArgument(destinationArgument);

        retryCommand.SetHandler((source, destination) =>
        {
            HandleRetryCommand(source, destination);
        }, sourceArgument, destinationArgument);

        rootCommand.AddCommand(retryCommand);

        // --- PARSE AND INVOKE ---
        return await rootCommand.InvokeAsync(args);
    }

    #region Command Handlers

    /// <summary>
    /// Handles the logic for a normal copy operation.
    /// </summary>
    private static void HandleCopyCommand(DirectoryInfo source, DirectoryInfo destination)
    {
        Console.WriteLine("Mode: Normal Copy");
        Initialize(destination.FullName, out string progressFilePath, out string errorFilePath);

        // 1. Load the set of files that were already copied successfully.
        var successfullyCopied = LoadSuccessfullyCopiedFiles(progressFilePath);
        if (successfullyCopied.Any())
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"Found {successfullyCopied.Count} files that were already copied. Resuming...");
            Console.ResetColor();
        }

        // 2. Discover all files in the source and filter out those already copied.
        Console.WriteLine("Discovering files in source directory...");
        var allSourceFiles = source.GetFiles("*.*", SearchOption.AllDirectories);
        var filesToCopy = allSourceFiles
            .Select(f => f.FullName)
            .Where(f => !successfullyCopied.Contains(f))
            .ToList();

        Console.WriteLine($"Found {allSourceFiles.Length} total files. {filesToCopy.Count} files need to be copied.\n");

        // 3. Run the copy session and get back any new errors.
        var newErrors = RunCopySession(filesToCopy, source.FullName, destination.FullName, progressFilePath);

        // 4. Append new errors to the error log.
        if (newErrors.Any())
        {
            File.AppendAllLines(errorFilePath, newErrors);
        }

        // 5. Print the final report.
        PrintFinalReport(allSourceFiles.Length, successfullyCopied.Count, filesToCopy.Count - newErrors.Count, newErrors.Count, errorFilePath);
    }

    /// <summary>
    /// Handles the logic for retrying failed copies.
    /// </summary>
    private static void HandleRetryCommand(DirectoryInfo source, DirectoryInfo destination)
    {
        Console.WriteLine("Mode: Retry Failed Files");
        Initialize(destination.FullName, out string progressFilePath, out string errorFilePath);

        // 1. Load the list of files that failed previously.
        var failedFilesToRetry = LoadFailedFiles(errorFilePath);
        if (!failedFilesToRetry.Any())
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("No failed files found in the error log. Nothing to retry.");
            Console.ResetColor();
            return;
        }
        Console.WriteLine($"Found {failedFilesToRetry.Count} files to retry.\n");

        // 2. Run the copy session with the list of failed files.
        var remainingErrors = RunCopySession(failedFilesToRetry, source.FullName, destination.FullName, progressFilePath);

        // 3. Overwrite the error log with files that are still failing.
        // This is the key logic: successful retries are removed from the error log.
        File.WriteAllLines(errorFilePath, remainingErrors);

        // 4. Print the final report.
        PrintRetryReport(failedFilesToRetry.Count, failedFilesToRetry.Count - remainingErrors.Count, remainingErrors.Count, errorFilePath);
    }

    #endregion

    #region Core Logic

    /// <summary>
    /// Executes the main loop for copying a given list of files.
    /// </summary>
    /// <param name="filesToProcess">The list of full file paths to process.</param>
    /// <returns>A list of strings, where each string is an error record for a failed file.</returns>
    private static List<string> RunCopySession(List<string> filesToProcess, string sourceBasePath, string destBasePath, string progressFilePath)
    {
        if (!filesToProcess.Any())
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("No files to process in this session.");
            Console.ResetColor();
            return new List<string>();
        }

        var errorsInThisSession = new List<string>();

        for (int i = 0; i < filesToProcess.Count; i++)
        {
            string sourceFile = filesToProcess[i];
            try
            {
                if (!File.Exists(sourceFile))
                {
                    throw new FileNotFoundException("Source file not found (it may have been moved or deleted).", sourceFile);
                }

                string relativePath = sourceFile.Substring(sourceBasePath.Length).TrimStart('\\', '/');
                string destinationFile = Path.Combine(destBasePath, relativePath);

                Directory.CreateDirectory(Path.GetDirectoryName(destinationFile));

                RenderProgressBar(i + 1, filesToProcess.Count, Path.GetFileName(sourceFile));

                File.Copy(sourceFile, destinationFile, overwrite: true);

                if (!VerifyFileCopy(sourceFile, destinationFile))
                {
                    throw new IOException("Verification failed. Copied file size does not match source.");
                }

                // IMPORTANT: Record progress immediately after success.
                File.AppendAllText(progressFilePath, sourceFile + Environment.NewLine);
            }
            catch (Exception ex)
            {
                Console.WriteLine(); // Newline to preserve error message below progress bar
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"\nERROR processing {Path.GetFileName(sourceFile)}: {ex.Message}");
                Console.ResetColor();
                errorsInThisSession.Add($"{sourceFile}|{ex.Message}");
            }
        }
        return errorsInThisSession;
    }

    #endregion

    #region Helper Methods

    private static void Initialize(string destinationDirectory, out string progressFilePath, out string errorFilePath)
    {
        Console.OutputEncoding = Encoding.UTF8;
        Directory.CreateDirectory(destinationDirectory); // Ensure destination exists
        progressFilePath = Path.Combine(destinationDirectory, ProgressFileName);
        errorFilePath = Path.Combine(destinationDirectory, ErrorFileName);

        Console.WriteLine($"Destination: {destinationDirectory}");
        Console.WriteLine($"Progress Log: {progressFilePath}");
        Console.WriteLine($"Error Log:    {errorFilePath}");
        Console.WriteLine("--------------------------------------------------");
    }

    private static HashSet<string> LoadSuccessfullyCopiedFiles(string progressFilePath)
    {
        if (!File.Exists(progressFilePath)) return new HashSet<string>();
        return new HashSet<string>(File.ReadAllLines(progressFilePath), StringComparer.OrdinalIgnoreCase);
    }

    private static List<string> LoadFailedFiles(string errorFilePath)
    {
        if (!File.Exists(errorFilePath)) return new List<string>();
        return File.ReadAllLines(errorFilePath)
                   .Select(line => line.Split('|').FirstOrDefault())
                   .Where(filePath => !string.IsNullOrEmpty(filePath))
                   .ToList();
    }

    private static bool VerifyFileCopy(string sourceFile, string destinationFile)
    {
        return new FileInfo(sourceFile).Length == new FileInfo(destinationFile).Length;
    }

    private static void RenderProgressBar(int current, int total, string fileName)
    {
        const int barWidth = 30;
        Console.CursorVisible = false;
        float percentage = (float)current / total;
        int progressWidth = (int)(percentage * barWidth);

        Console.Write("\r[");
        Console.BackgroundColor = ConsoleColor.Green;
        Console.Write(new string(' ', progressWidth));
        Console.BackgroundColor = ConsoleColor.Gray;
        Console.Write(new string(' ', barWidth - progressWidth));
        Console.ResetColor();

        int maxFileNameLength = Console.WindowWidth - barWidth - 25;
        if (maxFileNameLength < 10) maxFileNameLength = 10;
        if (fileName.Length > maxFileNameLength)
        {
            fileName = "..." + fileName.Substring(fileName.Length - maxFileNameLength + 3);
        }

        Console.Write($"] {current}/{total} ({percentage:P0}) - Processing: {fileName.PadRight(maxFileNameLength)}");
    }

    private static void PrintFinalReport(int total, int skipped, int copied, int errors, string errorPath)
    {
        Console.WriteLine("\n\n--------------------------------------------------");
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("Copy operation finished.");
        Console.WriteLine($"Total files in source:      {total}");
        Console.WriteLine($"Skipped (already copied):   {skipped}");
        Console.WriteLine($"Successfully copied now:    {copied}");
        Console.WriteLine($"Errors during this session: {errors}");
        if (errors > 0)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"\nDetails for failed files are in: {errorPath}");
        }
        Console.ResetColor();
    }

    private static void PrintRetryReport(int attempted, int copied, int errors, string errorPath)
    {
        Console.WriteLine("\n\n--------------------------------------------------");
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("Retry operation finished.");
        Console.WriteLine($"Files attempted to retry:   {attempted}");
        Console.WriteLine($"Successfully copied:        {copied}");
        Console.WriteLine($"Still failing:              {errors}");
        if (errors > 0)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"\nThe remaining failed files are still listed in: {errorPath}");
        }
        else if (attempted > 0)
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"\nSuccess! All previously failed files have been copied.");
        }
        Console.ResetColor();
    }

    #endregion
}
