using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using Microsoft.Win32;

namespace GIDE
{
    /// <summary>
    /// Manages runtime dependencies required by GIDE's native components (llama-server.exe).
    /// Detects missing VC++ Redistributable, downloads and installs it silently if needed,
    /// and provides PATH sanitization to avoid DLL conflicts with other installations (Python, etc.).
    /// </summary>
    public static class RuntimeDependencyManager
    {
        private const string VC_REDIST_URL_X64 = "https://aka.ms/vs/17/release/vc_redist.x64.exe";

        /// <summary>
        /// The result of a dependency check/install operation.
        /// </summary>
        public class DependencyResult
        {
            public bool Success { get; set; }
            public string Message { get; set; }
            public bool WasInstalled { get; set; } // True if we had to install something
            public bool NeedsRestart { get; set; } // True if a restart is recommended after install
        }

        /// <summary>
        /// Check if the Visual C++ 2015-2022 Redistributable (x64) is installed.
        /// Checks multiple registry locations for robustness.
        /// </summary>
        public static bool IsVCRedistInstalled()
        {
            // Method 1: Check the official VC++ Runtimes registry key
            try
            {
                using (var key = Registry.LocalMachine.OpenSubKey(
                    @"SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\X64"))
                {
                    if (key != null)
                    {
                        object majorObj = key.GetValue("Major");
                        if (majorObj != null)
                        {
                            int major = Convert.ToInt32(majorObj);
                            if (major >= 14) return true;
                        }
                        else
                        {
                            // Key exists but no Major value - still likely installed
                            return true;
                        }
                    }
                }
            }
            catch { }

            // Method 2: Check Wow6432Node (32-bit registry view on 64-bit OS)
            try
            {
                using (var key = Registry.LocalMachine.OpenSubKey(
                    @"SOFTWARE\WOW6432Node\Microsoft\VisualStudio\14.0\VC\Runtimes\X64"))
                {
                    if (key != null) return true;
                }
            }
            catch { }

            // Method 3: Check Uninstall entries for VC++ 2015-2022
            try
            {
                string[] uninstallPaths = {
                    @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
                    @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"
                };

                foreach (string basePath in uninstallPaths)
                {
                    using (var baseKey = Registry.LocalMachine.OpenSubKey(basePath))
                    {
                        if (baseKey == null) continue;
                        foreach (string subKeyName in baseKey.GetSubKeyNames())
                        {
                            try
                            {
                                using (var subKey = baseKey.OpenSubKey(subKeyName))
                                {
                                    if (subKey == null) continue;
                                    string displayName = subKey.GetValue("DisplayName") as string;
                                    if (displayName == null) continue;

                                    // Match "Microsoft Visual C++ 2015-2022 Redistributable (x64)"
                                    // or similar variants
                                    if (displayName.Contains("Visual C++") &&
                                        displayName.Contains("x64") &&
                                        (displayName.Contains("2015") || displayName.Contains("2017") ||
                                         displayName.Contains("2019") || displayName.Contains("2022")))
                                    {
                                        return true;
                                    }
                                }
                            }
                            catch { }
                        }
                    }
                }
            }
            catch { }

            // Method 4: Check if vcruntime140.dll exists in System32 (ultimate fallback)
            try
            {
                string sys32 = Environment.GetFolderPath(Environment.SpecialFolder.System);
                string dllPath = Path.Combine(sys32, "vcruntime140.dll");
                if (File.Exists(dllPath))
                {
                    // Verify it's a reasonable size (not corrupted)
                    FileInfo fi = new FileInfo(dllPath);
                    if (fi.Length > 50000) // Should be ~90KB+
                        return true;
                }
            }
            catch { }

            return false;
        }

        /// <summary>
        /// Check and install the VC++ Redistributable if missing.
        /// Downloads from Microsoft and installs silently.
        /// Returns a result with status and message for the UI to display.
        /// </summary>
        public static DependencyResult EnsureVCRedistInstalled()
        {
            if (IsVCRedistInstalled())
            {
                return new DependencyResult
                {
                    Success = true,
                    Message = "Visual C++ Redistributable is already installed.",
                    WasInstalled = false
                };
            }

            Console.WriteLine("  [!] Visual C++ Redistributable not found. Installing...");

            // Try to download and install
            string tempDir = Path.Combine(Path.GetTempPath(), "GIDE_setup");
            string installerPath = Path.Combine(tempDir, "vc_redist.x64.exe");

            try
            {
                if (!Directory.Exists(tempDir))
                    Directory.CreateDirectory(tempDir);

                // Download the redistributable
                ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;

                using (var client = new WebClient())
                {
                    client.Headers[HttpRequestHeader.UserAgent] = "GIDE";
                    Console.WriteLine("  Downloading Visual C++ Redistributable...");
                    client.DownloadFile(VC_REDIST_URL_X64, installerPath);
                }

                if (!File.Exists(installerPath))
                {
                    return new DependencyResult
                    {
                        Success = false,
                        Message = "Failed to download Visual C++ Redistributable.\n\n" +
                                  "Please install it manually from:\n" + VC_REDIST_URL_X64
                    };
                }

                // Install silently
                Console.WriteLine("  Installing Visual C++ Redistributable...");
                var psi = new ProcessStartInfo
                {
                    FileName = installerPath,
                    Arguments = "/install /quiet /norestart",
                    UseShellExecute = true, // Needs elevation
                    Verb = "runas",         // Request admin
                    CreateNoWindow = true
                };

                try
                {
                    using (var process = Process.Start(psi))
                    {
                        process.WaitForExit(120000); // 2 minute timeout

                        if (process.ExitCode == 0 || process.ExitCode == 3010) // 3010 = success, needs reboot
                        {
                            Console.WriteLine("   Visual C++ Redistributable installed successfully.");
                            return new DependencyResult
                            {
                                Success = true,
                                Message = "Visual C++ Redistributable installed successfully.",
                                WasInstalled = true,
                                NeedsRestart = process.ExitCode == 3010
                            };
                        }
                        else
                        {
                            return new DependencyResult
                            {
                                Success = false,
                                Message = "VC++ Redistributable installer exited with code " + process.ExitCode + ".\n\n" +
                                          "Please install it manually from:\n" + VC_REDIST_URL_X64
                            };
                        }
                    }
                }
                catch (System.ComponentModel.Win32Exception ex)
                {
                    if (ex.NativeErrorCode == 1223)
                    {
                        // ERROR_CANCELLED - User declined UAC elevation
                        return new DependencyResult
                        {
                            Success = false,
                            Message = "Administrator permission is required to install the Visual C++ Redistributable.\n\n" +
                                      "GIDE needs this component to run its AI engine (llama-server.exe).\n\n" +
                                      "You can install it manually by downloading from:\n" + VC_REDIST_URL_X64
                        };
                    }
                    throw; // Re-throw if it's a different Win32 error
                }
            }
            catch (Exception ex)
            {
                return new DependencyResult
                {
                    Success = false,
                    Message = "Failed to install Visual C++ Redistributable: " + ex.Message + "\n\n" +
                              "Please install it manually from:\n" + VC_REDIST_URL_X64
                };
            }
            finally
            {
                // Cleanup temp file
                try
                {
                    if (File.Exists(installerPath))
                        File.Delete(installerPath);
                    if (Directory.Exists(tempDir))
                        Directory.Delete(tempDir, false);
                }
                catch { }
            }
        }

        /// <summary>
        /// Build a sanitized PATH for launching llama-server.exe.
        /// Removes paths containing Python or other known DLL conflict sources,
        /// ensuring the system vcruntime140.dll is found first.
        /// </summary>
        public static string GetSanitizedPath(string binDir)
        {
            // Start with the bin directory itself (so llama.cpp's bundled DLLs are found first)
            var cleanPaths = new System.Collections.Generic.List<string>();
            cleanPaths.Add(binDir);

            // Add System32 and SysWOW64 (core Windows DLLs)
            string sys32 = Environment.GetFolderPath(Environment.SpecialFolder.System);
            string sysWow = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "SysWOW64");
            string winDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);

            cleanPaths.Add(sys32);
            if (Directory.Exists(sysWow))
                cleanPaths.Add(sysWow);
            cleanPaths.Add(winDir);

            // From the current PATH, include only safe directories
            // Exclude: Python dirs, Anaconda, Miniconda, and any dir with a known-conflicting vcruntime140.dll
            string currentPath = Environment.GetEnvironmentVariable("PATH") ?? "";
            string[] pathEntries = currentPath.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);

            foreach (string entry in pathEntries)
            {
                string trimmed = entry.Trim();
                if (string.IsNullOrEmpty(trimmed)) continue;

                // Skip if already added
                bool alreadyAdded = false;
                foreach (string existing in cleanPaths)
                {
                    if (string.Equals(existing, trimmed, StringComparison.OrdinalIgnoreCase))
                    {
                        alreadyAdded = true;
                        break;
                    }
                }
                if (alreadyAdded) continue;

                // Check if this path contains a known conflicting DLL source
                string lowerPath = trimmed.ToLowerInvariant();
                if (IsConflictingPath(lowerPath))
                    continue;

                cleanPaths.Add(trimmed);
            }

            return string.Join(";", cleanPaths);
        }

        /// <summary>
        /// Checks if a given PATH entry is known to contain conflicting vcruntime140.dll copies.
        /// </summary>
        private static bool IsConflictingPath(string lowerPath)
        {
            // Python installations are the most common source of DLL conflicts
            if (lowerPath.Contains("python"))
                return true;

            // Anaconda/Miniconda also bundle their own vcruntime
            if (lowerPath.Contains("anaconda") || lowerPath.Contains("miniconda") || lowerPath.Contains("conda"))
                return true;

            // Some Ruby, Perl, and other language installations also bundle vcruntime
            if (lowerPath.Contains("\\ruby") && lowerPath.Contains("\\bin"))
                return true;

            // Strawberry Perl
            if (lowerPath.Contains("strawberry"))
                return true;

            // Check if this specific directory has a vcruntime140.dll that might be corrupted
            // (Only do this for non-system directories)
            if (!lowerPath.Contains("\\windows\\") && !lowerPath.Contains("\\system32"))
            {
                try
                {
                    string dllInPath = Path.Combine(lowerPath, "vcruntime140.dll");
                    if (File.Exists(dllInPath))
                    {
                        // This directory has its own vcruntime140.dll - skip it to avoid conflicts
                        return true;
                    }
                }
                catch { } // Can't access directory - skip check
            }

            return false;
        }

        /// <summary>
        /// Validates that llama-server.exe can actually load by running it with --version.
        /// This catches DLL issues before the actual server launch.
        /// Returns null on success, or an error message describing the DLL problem.
        /// </summary>
        public static string ValidateLlamaServer(string binDir)
        {
            string serverExe = Path.Combine(binDir, "llama-server.exe");
            if (!File.Exists(serverExe))
                return "llama-server.exe not found at: " + serverExe;

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = serverExe,
                    Arguments = "--version",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    WorkingDirectory = binDir
                };

                // Set sanitized PATH to avoid DLL conflicts
                psi.EnvironmentVariables["PATH"] = GetSanitizedPath(binDir);

                using (var process = Process.Start(psi))
                {
                    string stdout = process.StandardOutput.ReadToEnd();
                    string stderr = process.StandardError.ReadToEnd();
                    process.WaitForExit(10000); // 10 second timeout

                    if (process.ExitCode == 0 || !string.IsNullOrEmpty(stdout))
                    {
                        // Server binary loads fine
                        return null;
                    }

                    // Non-zero exit might be because --version isn't supported (older builds)
                    // If we got here without a crash, the DLLs loaded fine
                    if (process.ExitCode == 1 && string.IsNullOrEmpty(stderr))
                        return null;

                    // Check for DLL-related error messages
                    string combined = (stdout + " " + stderr).ToLowerInvariant();
                    if (combined.Contains("vcruntime") || combined.Contains("bad image") ||
                        combined.Contains("0xc000012f") || combined.Contains("not designed to run"))
                    {
                        return "DLL_ERROR";
                    }

                    // Other error - probably fine for DLL purposes
                    return null;
                }
            }
            catch (System.ComponentModel.Win32Exception ex)
            {
                // This is the typical "Bad Image" exception (error code 193 or similar)
                string msg = ex.Message.ToLowerInvariant();
                if (msg.Contains("bad image") || msg.Contains("not a valid win32") ||
                    ex.NativeErrorCode == 193 || ex.NativeErrorCode == 216)
                {
                    return "DLL_ERROR";
                }
                return "Failed to validate llama-server: " + ex.Message;
            }
            catch (Exception ex)
            {
                return "Failed to validate llama-server: " + ex.Message;
            }
        }

        /// <summary>
        /// Full dependency check and repair workflow.
        /// Call this before starting llama-server.exe.
        /// Returns a DependencyResult indicating success/failure.
        /// </summary>
        public static DependencyResult EnsureAllDependencies(string binDir)
        {
            // Step 1: Quick check - validate the binary can load
            string validationError = ValidateLlamaServer(binDir);

            if (validationError == null)
            {
                // Everything works, no action needed
                return new DependencyResult { Success = true, Message = "All dependencies OK." };
            }

            if (validationError == "DLL_ERROR")
            {
                // DLL problem detected - check and install VC++ Redist
                Console.WriteLine("  [!] DLL conflict detected. Checking Visual C++ Redistributable...");

                var result = EnsureVCRedistInstalled();
                if (!result.Success)
                    return result;

                // After installing, validate again
                validationError = ValidateLlamaServer(binDir);
                if (validationError == null)
                {
                    return new DependencyResult
                    {
                        Success = true,
                        Message = "Dependencies repaired successfully.",
                        WasInstalled = true
                    };
                }

                // Still failing - the DLL conflict is from PATH (e.g., Python's corrupted copy)
                // The sanitized PATH in StartServer should fix this at runtime
                Console.WriteLine("  [!] DLL still conflicting after VC++ install - will use sanitized PATH.");
                return new DependencyResult
                {
                    Success = true, // Will work with sanitized PATH
                    Message = "VC++ installed. PATH sanitization will be applied at runtime.",
                    WasInstalled = result.WasInstalled
                };
            }

            // Non-DLL error (e.g., binary not found) - not our problem here
            if (validationError.StartsWith("llama-server.exe not found"))
            {
                return new DependencyResult { Success = true, Message = "Server binary not yet downloaded." };
            }

            // Unknown error
            return new DependencyResult
            {
                Success = true, // Optimistic - let StartServer handle it
                Message = validationError
            };
        }

        /// <summary>
        /// Get a user-friendly explanation of the DLL error for display in the GUI.
        /// </summary>
        public static string GetDllErrorExplanation()
        {
            return "GIDE's AI engine (llama-server.exe) requires the Microsoft Visual C++ Redistributable.\n\n" +
                   "This error typically occurs when:\n" +
                   "- The Visual C++ Redistributable is not installed\n" +
                   "- Another program (like Python) has a corrupted vcruntime140.dll in your PATH\n\n" +
                   "To fix this:\n" +
                   "1. Download and install the VC++ Redistributable from:\n" +
                   "   https://aka.ms/vs/17/release/vc_redist.x64.exe\n\n" +
                   "2. If the error persists, check if Python or Anaconda is in your PATH\n" +
                   "   and try reinstalling/repairing that installation.\n\n" +
                   "GIDE will attempt to work around PATH conflicts automatically.";
        }
    }
}
