using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace GIDE
{
    public class ToolCall
    {
        public string Type;
        public string Path;
        public string Content;
        public string Command;
        public string Destination; // For RENAME: new path
        public string OldText;     // For PATCH: text to find
        public string NewText;     // For PATCH: replacement text
    }

    public static class ToolParser
    {
        public static List<ToolCall> Parse(string response)
        {
            var tools = new List<ToolCall>();

            MatchCollection matches = Regex.Matches(response,
                @"<<<TOOL:(.+?)>>>(.*?)<<<END_TOOL>>>",
                RegexOptions.Singleline);

            foreach (Match match in matches)
            {
                var tool = new ToolCall();
                tool.Type = match.Groups[1].Value.Trim().ToUpper();
                string body = match.Groups[2].Value;

                switch (tool.Type)
                {
                    case "WRITE":
                    {
                        // First line is the file path
                        string trimmed = body.TrimStart('\r', '\n', ' ');
                        int newline = trimmed.IndexOfAny(new char[] { '\n', '\r' });
                        if (newline < 0)
                        {
                            tool.Path = trimmed.Trim();
                            tool.Content = "";
                        }
                        else
                        {
                            tool.Path = trimmed.Substring(0, newline).Trim();
                            string rest = trimmed.Substring(newline + 1);

                            int contentStart = rest.IndexOf("<<<CONTENT>>>");
                            int contentEnd   = rest.IndexOf("<<<END_CONTENT>>>");

                            if (contentStart >= 0 && contentEnd > contentStart)
                            {
                                // Extract content between markers, preserving internal whitespace
                                // but stripping exactly one leading newline after the marker
                                string raw = rest.Substring(contentStart + 13, contentEnd - contentStart - 13);
                                if (raw.StartsWith("\r\n")) raw = raw.Substring(2);
                                else if (raw.StartsWith("\n"))  raw = raw.Substring(1);
                                // Strip only trailing newline added by the model after <<<END_CONTENT>>>
                                tool.Content = raw.TrimEnd('\r', '\n');
                            }
                            else
                            {
                                // Fallback: Try to extract from markdown code blocks (```lang ... ```)
                                // This handles cases where the model uses markdown instead of <<<CONTENT>>>
                                Match mdMatch = Regex.Match(rest, @"```[a-zA-Z]*\r?\n(.*?)```", RegexOptions.Singleline);
                                if (mdMatch.Success)
                                {
                                    tool.Content = mdMatch.Groups[1].Value.TrimEnd('\r', '\n');
                                }
                                else
                                {
                                    // No content markers and no markdown - use everything after the path line
                                    tool.Content = rest.TrimEnd('\r', '\n');
                                }
                            }
                        }
                        break;
                    }

                    case "READ":
                    case "LIST":
                    case "DELETE":
                        tool.Path = body.Trim();
                        break;

                    case "PATCH":
                    {
                        // Format:
                        // path/to/file.cs
                        // <<<OLD>>>
                        // text to find
                        // <<<NEW>>>
                        // replacement text
                        // <<<END_PATCH>>>
                        string trimmedPatch = body.TrimStart('\r', '\n', ' ');
                        int firstNewline = trimmedPatch.IndexOfAny(new char[] { '\n', '\r' });
                        if (firstNewline < 0) break;

                        tool.Path = trimmedPatch.Substring(0, firstNewline).Trim();
                        string patchBody = trimmedPatch.Substring(firstNewline + 1);

                        int oldStart = patchBody.IndexOf("<<<OLD>>>");
                        int newStart = patchBody.IndexOf("<<<NEW>>>");
                        int endPatch = patchBody.IndexOf("<<<END_PATCH>>>");

                        if (oldStart >= 0 && newStart > oldStart)
                        {
                            // Extract OLD text
                            string oldRaw = patchBody.Substring(oldStart + 9,
                                newStart - oldStart - 9);
                            if (oldRaw.StartsWith("\r\n")) oldRaw = oldRaw.Substring(2);
                            else if (oldRaw.StartsWith("\n")) oldRaw = oldRaw.Substring(1);
                            tool.OldText = oldRaw.TrimEnd('\r', '\n');

                            // Extract NEW text
                            int newContentStart = newStart + 9;
                            int newContentEnd = endPatch >= 0 ? endPatch : patchBody.Length;
                            string newRaw = patchBody.Substring(newContentStart,
                                newContentEnd - newContentStart);
                            if (newRaw.StartsWith("\r\n")) newRaw = newRaw.Substring(2);
                            else if (newRaw.StartsWith("\n")) newRaw = newRaw.Substring(1);
                            tool.NewText = newRaw.TrimEnd('\r', '\n');
                        }
                        break;
                    }

                    case "RENAME":
                    {
                        // Two lines: old path, then new path
                        string trimmedRename = body.TrimStart('\r', '\n', ' ');
                        string[] renameLines = trimmedRename.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
                        if (renameLines.Length >= 2)
                        {
                            tool.Path = renameLines[0].Trim();
                            tool.Destination = renameLines[1].Trim();
                        }
                        else if (renameLines.Length == 1)
                        {
                            tool.Path = renameLines[0].Trim();
                        }
                        break;
                    }

                    case "RUN":
                        tool.Command = body.Trim();
                        break;
                }

                tools.Add(tool);
            }

            return tools;
        }

        public static string StripTools(string response)
        {
            // Remove all tool blocks
            string result = Regex.Replace(response,
                @"<<<TOOL:(.+?)>>>(.*?)<<<END_TOOL>>>",
                "", RegexOptions.Singleline);

            return result.Trim();
        }

        /// <summary>
        /// Attempts to convert a response that contains markdown code blocks (but no tool markers)
        /// into proper tool format. Returns the converted response if successful, or null if no conversion possible.
        /// </summary>
        public static string TryConvertMarkdownToTools(string response, string workDir)
        {
            // If already has tool markers, no conversion needed
            if (response.Contains("<<<TOOL:"))
                return null;

            // Look for markdown code blocks with file paths mentioned nearby
            var conversions = new List<string>();
            
            // Try to find code blocks with file paths
            MatchCollection codeBlocks = Regex.Matches(response, 
                @"```([a-zA-Z#]*)\r?\n(.*?)```", 
                RegexOptions.Singleline);

            if (codeBlocks.Count == 0)
                return null;

            // First pass: collect all file paths mentioned anywhere in the response
            var mentionedPaths = new List<string>();
            MatchCollection allPathMentions = Regex.Matches(response,
                @"[`'\""]([\\\/\w.-]+\.(?:cs|js|ts|py|java|go|rs|cpp|c|h|rb|php|swift|kt|vb|fs|jsx|tsx|vue|svelte|html|css|scss|json|xml|yaml|yml|toml|sql|sh|bat|ps1|md|txt))[`'\"":,\s]",
                RegexOptions.IgnoreCase);
            foreach (Match m in allPathMentions)
                mentionedPaths.Add(m.Groups[1].Value.Replace("\\", "/"));

            int pathIndex = 0;

            foreach (Match block in codeBlocks)
            {
                string lang = block.Groups[1].Value.ToLower();
                string code = block.Groups[2].Value;

                // Skip very short code blocks (likely not full files)
                if (code.Length < 50)
                    continue;

                // Try to find a file path mentioned near this code block
                int blockPos = block.Index;
                string textBefore = response.Substring(Math.Max(0, blockPos - 500), Math.Min(500, blockPos));
                
                // Look for file paths in the text before the code block
                Match pathMatch = Regex.Match(textBefore, 
                    @"[`'\""]([\w\\/.-]+\.(?:cs|js|ts|py|java|go|rs|cpp|c|h|rb|php|swift|kt|vb|fs|jsx|tsx|vue|svelte|html|css|scss|json|xml|yaml|yml|toml|sql|sh|bat|ps1|md|txt))[`'\"":]",
                    RegexOptions.IgnoreCase | RegexOptions.RightToLeft);

                if (pathMatch.Success)
                {
                    string filePath = pathMatch.Groups[1].Value.Replace("\\", "/");
                    conversions.Add("<<<TOOL:WRITE>>>\n" + filePath + "\n<<<CONTENT>>>\n" + code.TrimEnd('\r', '\n') + "\n<<<END_CONTENT>>>\n<<<END_TOOL>>>");
                }
                else if (pathIndex < mentionedPaths.Count)
                {
                    // Use the next mentioned path from the full response as fallback
                    // (model often lists files at the top then shows code blocks in order)
                    string filePath = mentionedPaths[pathIndex];
                    pathIndex++;
                    conversions.Add("<<<TOOL:WRITE>>>\n" + filePath + "\n<<<CONTENT>>>\n" + code.TrimEnd('\r', '\n') + "\n<<<END_CONTENT>>>\n<<<END_TOOL>>>");
                }
                else if (!string.IsNullOrEmpty(lang))
                {
                    // Try to infer file path from language and code content
                    string inferredPath = InferFilePath(lang, code, workDir);
                    if (!string.IsNullOrEmpty(inferredPath))
                    {
                        conversions.Add("<<<TOOL:WRITE>>>\n" + inferredPath + "\n<<<CONTENT>>>\n" + code.TrimEnd('\r', '\n') + "\n<<<END_CONTENT>>>\n<<<END_TOOL>>>");
                    }
                }
            }

            if (conversions.Count > 0)
            {
                return string.Join("\n\n", conversions.ToArray()) + "\n\n[Auto-converted from markdown]";
            }

            return null;
        }

        private static string InferFilePath(string lang, string code, string workDir)
        {
            // Try to infer file path from namespace/class declarations
            if (lang == "csharp" || lang == "cs" || lang == "c#")
            {
                // Look for namespace and class
                Match nsMatch = Regex.Match(code, @"namespace\s+([\w.]+)");
                Match classMatch = Regex.Match(code, @"(?:public|internal|private)?\s*(?:static|abstract|sealed|partial)?\s*class\s+(\w+)");
                
                if (classMatch.Success)
                {
                    string className = classMatch.Groups[1].Value;
                    // Try to find existing file with this class name
                    try
                    {
                        string[] files = System.IO.Directory.GetFiles(workDir, className + ".cs", System.IO.SearchOption.AllDirectories);
                        if (files.Length == 1)
                        {
                            // Return relative path
                            return files[0].Substring(workDir.Length).TrimStart(System.IO.Path.DirectorySeparatorChar).Replace("\\", "/");
                        }
                        else if (files.Length > 1)
                        {
                            // Ambiguous file name, don't guess
                            return null;
                        }
                    }
                    catch { }
                    
                    // Default to src folder
                    return "src/" + className + ".cs";
                }
            }

            return null;
        }

        /// <summary>
        /// Detects if the model's response contains code blocks that weren't written to disk.
        /// Used to trigger the nudge flow.
        /// </summary>
        public static bool HasUnwrittenCode(string response)
        {
            if (string.IsNullOrEmpty(response)) return false;

            // If it already has tool markers, the code was handled
            if (response.Contains("<<<TOOL:WRITE>>>")) return false;

            // Check for substantial markdown code blocks (at least 5 lines)
            MatchCollection blocks = Regex.Matches(response, @"```[a-zA-Z]*\r?\n(.*?)```", RegexOptions.Singleline);
            foreach (Match block in blocks)
            {
                string code = block.Groups[1].Value;
                int lineCount = code.Split('\n').Length;
                if (lineCount >= 5)
                    return true;
            }

            return false;
        }
    }
}
