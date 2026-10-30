using System;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace GIDE
{
    public class ToolExecutor
    {
        private string _workDir;

        public ToolExecutor(string workDir)
        {
            _workDir = Path.GetFullPath(workDir);
        }

        public string Execute(ToolCall tool)
        {
            try
            {
                switch (tool.Type.ToUpper())
                {
                    case "WRITE":  return WriteFile(tool.Path, tool.Content);
                    case "READ":   return ReadFile(tool.Path);
                    case "RUN":    return RunCommand(tool.Command);
                    case "LIST":   return ListFiles(tool.Path);
                    case "DELETE": return DeleteFile(tool.Path);
                    case "RENAME": return RenameFile(tool.Path, tool.Destination);
                    case "PATCH":  return PatchFile(tool.Path, tool.OldText, tool.NewText);
                    default:       return "[ERROR] Unknown tool: " + tool.Type;
                }
            }
            catch (Exception ex)
            {
                return "[ERROR] " + tool.Type + ": " + ex.Message;
            }
        }

        private string WriteFile(string relativePath, string content)
        {
            if (string.IsNullOrWhiteSpace(relativePath))
                return "[WRITE ERROR] No file path provided";

            if (content == null)
                content = "";

            // Resolve full path safely
            relativePath = relativePath.Trim().Replace('/', Path.DirectorySeparatorChar);
            string fullPath = Path.GetFullPath(Path.Combine(_workDir, relativePath));

            // Safety: don't write outside the work directory
            if (!fullPath.StartsWith(_workDir, StringComparison.OrdinalIgnoreCase))
                return "[WRITE ERROR] Path escapes work directory: " + relativePath;

            // Ensure parent directory exists
            string dir = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            // Clear read-only attribute if set - common cause of silent write failures
            if (File.Exists(fullPath))
            {
                try
                {
                    FileAttributes attrs = File.GetAttributes(fullPath);
                    if ((attrs & FileAttributes.ReadOnly) == FileAttributes.ReadOnly)
                        File.SetAttributes(fullPath, attrs & ~FileAttributes.ReadOnly);
                }
                catch { /* non-fatal, attempt write anyway */ }
            }

            // Write with retry - handles transient locks (antivirus, indexers, etc.)
            Exception lastEx = null;
            for (int attempt = 1; attempt <= 3; attempt++)
            {
                try
                {
                    // Write to a temp file first, then atomic-replace
                    string tempPath = fullPath + ".gide_tmp";
                    File.WriteAllText(tempPath, content, new UTF8Encoding(false));

                    // Delete destination and move temp into place
                    if (File.Exists(fullPath))
                        File.Delete(fullPath);

                    File.Move(tempPath, fullPath);

                    long written = new FileInfo(fullPath).Length;
                    return string.Format("[WRITE OK] {0} ({1} bytes)", relativePath, written);
                }
                catch (Exception ex)
                {
                    lastEx = ex;
                    if (attempt < 3)
                        System.Threading.Thread.Sleep(300 * attempt);
                }
            }

            return "[WRITE FAILED] " + relativePath + " - " + (lastEx != null ? lastEx.Message : "unknown error");
        }

        private string PatchFile(string relativePath, string oldText, string newText)
        {
            if (string.IsNullOrWhiteSpace(relativePath))
                return "[PATCH ERROR] No file path provided";
            if (oldText == null)
                return "[PATCH ERROR] No OLD text provided";
            if (newText == null)
                newText = ""; // Allow replacing with empty (deletion)

            relativePath = relativePath.Trim().Replace('/', Path.DirectorySeparatorChar);
            string fullPath = Path.GetFullPath(Path.Combine(_workDir, relativePath));

            // Safety: don't patch outside the work directory
            if (!fullPath.StartsWith(_workDir, StringComparison.OrdinalIgnoreCase))
                return "[PATCH ERROR] Path escapes work directory: " + relativePath;

            if (!File.Exists(fullPath))
                return "[PATCH ERROR] File not found: " + relativePath;

            string content;
            try
            {
                content = File.ReadAllText(fullPath, Encoding.UTF8);
            }
            catch (Exception ex)
            {
                return "[PATCH ERROR] Cannot read file: " + ex.Message;
            }

            // Try exact match first
            int matchIndex = content.IndexOf(oldText, StringComparison.Ordinal);

            // If exact match fails, try with normalized line endings
            if (matchIndex < 0)
            {
                string normalizedContent = content.Replace("\r\n", "\n");
                string normalizedOld = oldText.Replace("\r\n", "\n");
                int normIndex = normalizedContent.IndexOf(normalizedOld, StringComparison.Ordinal);

                if (normIndex >= 0)
                {
                    // Found with normalized endings - do the replacement on normalized, then restore
                    string normalizedNew = newText.Replace("\r\n", "\n");
                    string result = normalizedContent.Substring(0, normIndex) +
                                    normalizedNew +
                                    normalizedContent.Substring(normIndex + normalizedOld.Length);

                    // Restore original line ending style
                    if (content.Contains("\r\n"))
                        result = result.Replace("\n", "\r\n");

                    return WritePatchResult(fullPath, relativePath, result);
                }

                // Try trimmed-line matching (ignore leading/trailing whitespace per line)
                int fuzzyIndex = FuzzyFindPatch(content, oldText);
                if (fuzzyIndex >= 0)
                {
                    // Fuzzy matched - calculate actual length of matched region in original
                    string[] oldLines = oldText.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
                    string[] contentLines = content.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
                    string lineEnding = content.Contains("\r\n") ? "\r\n" : "\n";

                    // Find which content line the fuzzyIndex corresponds to
                    int lineStart = 0;
                    int charCount = 0;
                    for (int i = 0; i < contentLines.Length; i++)
                    {
                        if (charCount >= fuzzyIndex) { lineStart = i; break; }
                        charCount += contentLines[i].Length + lineEnding.Length;
                    }

                    // Calculate actual matched length in original content
                    int matchLen = 0;
                    for (int i = lineStart; i < lineStart + oldLines.Length && i < contentLines.Length; i++)
                    {
                        matchLen += contentLines[i].Length;
                        if (i < lineStart + oldLines.Length - 1)
                            matchLen += lineEnding.Length;
                    }

                    string fuzzyPatched = content.Substring(0, fuzzyIndex) +
                                          newText +
                                          content.Substring(fuzzyIndex + matchLen);
                    return WritePatchResult(fullPath, relativePath, fuzzyPatched);
                }
            }

            if (matchIndex < 0)
            {
                // Show what we were looking for to help debugging
                string preview = oldText.Length > 80 ? oldText.Substring(0, 80) + "..." : oldText;
                return "[PATCH FAILED] Text not found in " + relativePath +
                       ". Looking for: \"" + preview + "\"" +
                       "\n\nHint: Use <<<TOOL:WRITE>>> with full file content instead.";
            }

            // Check for multiple matches (ambiguous)
            int secondMatch = content.IndexOf(oldText, matchIndex + 1, StringComparison.Ordinal);
            if (secondMatch >= 0)
            {
                return "[PATCH FAILED] Multiple matches found in " + relativePath +
                       ". The OLD text is ambiguous - add more context lines to make it unique." +
                       "\n\nHint: Include 2-3 lines before and after the change.";
            }

            // Apply the patch
            string patched = content.Substring(0, matchIndex) +
                             newText +
                             content.Substring(matchIndex + oldText.Length);

            return WritePatchResult(fullPath, relativePath, patched);
        }

        private string WritePatchResult(string fullPath, string relativePath, string patched)
        {
            try
            {
                string tempPath = fullPath + ".gide_tmp";
                File.WriteAllText(tempPath, patched, new UTF8Encoding(false));

                if (File.Exists(fullPath))
                    File.Delete(fullPath);
                File.Move(tempPath, fullPath);

                return "[PATCH OK] " + relativePath;
            }
            catch (Exception ex)
            {
                return "[PATCH FAILED] " + relativePath + " - " + ex.Message;
            }
        }

        /// <summary>
        /// Fuzzy matching: tries to find the old text by trimming whitespace from each line.
        /// Handles cases where the model gets indentation slightly wrong.
        /// Returns the start char index in content, or -1 if not found.
        /// </summary>
        private static int FuzzyFindPatch(string content, string oldText)
        {
            // Normalize both to trimmed lines for comparison
            string[] oldLines = oldText.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
            string[] contentLines = content.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);

            if (oldLines.Length == 0 || oldLines.Length > contentLines.Length)
                return -1;

            // Trim each old line for comparison
            string[] oldTrimmed = new string[oldLines.Length];
            for (int i = 0; i < oldLines.Length; i++)
                oldTrimmed[i] = oldLines[i].Trim();

            // Skip empty-only searches
            bool allEmpty = true;
            foreach (var line in oldTrimmed)
                if (line.Length > 0) { allEmpty = false; break; }
            if (allEmpty) return -1;

            string lineEnding = content.Contains("\r\n") ? "\r\n" : "\n";

            // Slide through content looking for a match
            int charOffset = 0;
            for (int start = 0; start <= contentLines.Length - oldLines.Length; start++)
            {
                bool match = true;
                for (int j = 0; j < oldLines.Length; j++)
                {
                    if (contentLines[start + j].Trim() != oldTrimmed[j])
                    {
                        match = false;
                        break;
                    }
                }

                if (match)
                    return charOffset;

                charOffset += contentLines[start].Length + lineEnding.Length;
            }

            return -1;
        }

        private string ReadFile(string relativePath)
        {
            if (string.IsNullOrWhiteSpace(relativePath))
                return "[READ ERROR] No file path provided";

            relativePath = relativePath.Trim().Replace('/', Path.DirectorySeparatorChar);
            string fullPath = Path.GetFullPath(Path.Combine(_workDir, relativePath));

            if (!File.Exists(fullPath))
                return "[READ ERROR] File not found: " + relativePath;

            try
            {
                string text = File.ReadAllText(fullPath, Encoding.UTF8);
                return "[READ] " + relativePath + " (" + text.Length + " chars)\n" + text;
            }
            catch (Exception ex)
            {
                return "[READ ERROR] " + relativePath + " - " + ex.Message;
            }
        }

        private string RunCommand(string command)
        {
            if (string.IsNullOrWhiteSpace(command))
                return "[RUN ERROR] No command provided";

            try
            {
                var psi = new ProcessStartInfo("cmd.exe", "/c " + command);
                psi.WorkingDirectory = _workDir;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                psi.UseShellExecute = false;
                psi.StandardOutputEncoding = Encoding.UTF8;
                psi.StandardErrorEncoding = Encoding.UTF8;

                using (var process = Process.Start(psi))
                {
                    // Read both streams concurrently to avoid deadlock on large output
                    string stdout = "";
                    string stderr = "";
                    var outTask = System.Threading.Tasks.Task.Run(() => stdout = process.StandardOutput.ReadToEnd());
                    var errTask = System.Threading.Tasks.Task.Run(() => stderr = process.StandardError.ReadToEnd());

                    process.WaitForExit(30000); // 30s timeout
                    System.Threading.Tasks.Task.WaitAll(outTask, errTask);

                    string combined = stdout;
                    if (!string.IsNullOrEmpty(stderr))
                        combined += (combined.Length > 0 ? "\n" : "") + "[stderr] " + stderr;

                    int exitCode = process.ExitCode;
                    string status = exitCode == 0 ? "[RUN OK]" : "[RUN EXIT " + exitCode + "]";
                    return status + " " + command + "\n" + combined.Trim();
                }
            }
            catch (Exception ex)
            {
                return "[RUN ERROR] " + command + " - " + ex.Message;
            }
        }

        private string ListFiles(string path)
        {
            try
            {
                string targetPath = string.IsNullOrWhiteSpace(path)
                    ? _workDir
                    : Path.GetFullPath(Path.Combine(_workDir, path.Trim()));

                if (!Directory.Exists(targetPath))
                    return "[LIST ERROR] Directory not found: " + path;

                var sb = new StringBuilder();
                sb.AppendLine("[LIST] " + targetPath);

                foreach (string entry in Directory.GetFileSystemEntries(targetPath, "*", SearchOption.AllDirectories))
                {
                    string rel = entry.Substring(_workDir.Length).TrimStart(Path.DirectorySeparatorChar);
                    bool isDir = Directory.Exists(entry);
                    sb.AppendLine((isDir ? "[DIR]  " : "[FILE] ") + rel);
                }

                return sb.ToString().Trim();
            }
            catch (Exception ex)
            {
                return "[LIST ERROR] " + ex.Message;
            }
        }

        private string DeleteFile(string relativePath)
        {
            if (string.IsNullOrWhiteSpace(relativePath))
                return "[DELETE ERROR] No file path provided";

            relativePath = relativePath.Trim().Replace('/', Path.DirectorySeparatorChar);
            string fullPath = Path.GetFullPath(Path.Combine(_workDir, relativePath));

            // Safety: don't delete outside the work directory
            if (!fullPath.StartsWith(_workDir, StringComparison.OrdinalIgnoreCase))
                return "[DELETE ERROR] Path escapes work directory: " + relativePath;

            if (File.Exists(fullPath))
            {
                try
                {
                    // Clear read-only if set
                    FileAttributes attrs = File.GetAttributes(fullPath);
                    if ((attrs & FileAttributes.ReadOnly) == FileAttributes.ReadOnly)
                        File.SetAttributes(fullPath, attrs & ~FileAttributes.ReadOnly);

                    File.Delete(fullPath);
                    return "[DELETE OK] " + relativePath;
                }
                catch (Exception ex)
                {
                    return "[DELETE FAILED] " + relativePath + " - " + ex.Message;
                }
            }
            else if (Directory.Exists(fullPath))
            {
                try
                {
                    Directory.Delete(fullPath, recursive: true);
                    return "[DELETE OK] " + relativePath + " (directory)";
                }
                catch (Exception ex)
                {
                    return "[DELETE FAILED] " + relativePath + " - " + ex.Message;
                }
            }
            else
            {
                return "[DELETE ERROR] Not found: " + relativePath;
            }
        }

        private string RenameFile(string relativePath, string newRelativePath)
        {
            if (string.IsNullOrWhiteSpace(relativePath))
                return "[RENAME ERROR] No source path provided";
            if (string.IsNullOrWhiteSpace(newRelativePath))
                return "[RENAME ERROR] No destination path provided";

            relativePath = relativePath.Trim().Replace('/', Path.DirectorySeparatorChar);
            newRelativePath = newRelativePath.Trim().Replace('/', Path.DirectorySeparatorChar);

            string fullSource = Path.GetFullPath(Path.Combine(_workDir, relativePath));
            string fullDest = Path.GetFullPath(Path.Combine(_workDir, newRelativePath));

            // Safety: both paths must be within work directory
            if (!fullSource.StartsWith(_workDir, StringComparison.OrdinalIgnoreCase))
                return "[RENAME ERROR] Source escapes work directory: " + relativePath;
            if (!fullDest.StartsWith(_workDir, StringComparison.OrdinalIgnoreCase))
                return "[RENAME ERROR] Destination escapes work directory: " + newRelativePath;

            if (!File.Exists(fullSource) && !Directory.Exists(fullSource))
                return "[RENAME ERROR] Source not found: " + relativePath;

            if (File.Exists(fullDest) || Directory.Exists(fullDest))
                return "[RENAME ERROR] Destination already exists: " + newRelativePath;

            try
            {
                // Ensure destination directory exists
                string destDir = Path.GetDirectoryName(fullDest);
                if (!string.IsNullOrEmpty(destDir) && !Directory.Exists(destDir))
                    Directory.CreateDirectory(destDir);

                if (File.Exists(fullSource))
                    File.Move(fullSource, fullDest);
                else
                    Directory.Move(fullSource, fullDest);

                return "[RENAME OK] " + relativePath + " -> " + newRelativePath;
            }
            catch (Exception ex)
            {
                return "[RENAME FAILED] " + relativePath + " - " + ex.Message;
            }
        }
    }
}
