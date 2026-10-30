using System;
using System.Management;
using System.Runtime.InteropServices;
using System.Collections.Generic;

namespace GIDE
{
    /// <summary>
    /// DXGI Adapter memory structure for accurate VRAM detection
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct DXGI_ADAPTER_DESC
    {
        public uint VendorId;
        public uint DeviceId;
        public uint SubSysId;
        public uint Revision;
        public ulong DedicatedVideoMemory;
        public ulong DedicatedSystemMemory;
        public ulong SharedSystemMemory;
        public uint AdapterLuidLow;
        public int AdapterLuidHigh;
    }

    [ComImport, Guid("2411e7e1-12ac-4ccf-bd14-9798e8534dc0"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IDXGIFactory1
    {
        void EnumAdapters(uint Adapter, out IDXGIAdapter1 ppAdapter);
        void MakeWindowAssociation(IntPtr WindowHandle, uint Flags);
        void GetWindowAssociation(out IntPtr pWindowHandle);
        void CreateSwapChain(object pDevice, ref object pDesc, out object ppSwapChain);
        void CreateSoftwareAdapter(IntPtr Module, out IDXGIAdapter1 ppAdapter);
        void EnumAdapters1(uint Adapter, out IDXGIAdapter1 ppAdapter);
        bool IsCurrent();
    }

    [ComImport, Guid("29038f61-3839-4626-91fd-086879011a05"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IDXGIAdapter1
    {
        void EnumOutputs(uint Output, out object ppOutput);
        void GetDesc(out DXGI_ADAPTER_DESC pDesc);
        void CheckInterfaceSupport(ref Guid InterfaceName, out long pUMDVersion);
        void GetDesc1(out DXGI_ADAPTER_DESC pDesc);
    }

    public static class HardwareDetector
    {
        [DllImport("kernel32.dll")]
        static extern void GetSystemInfo(ref SYSTEM_INFO lpSystemInfo);

        [DllImport("kernel32.dll")]
        static extern void GetNativeSystemInfo(ref SYSTEM_INFO lpSystemInfo);

        [StructLayout(LayoutKind.Sequential)]
        struct SYSTEM_INFO
        {
            public ushort wProcessorArchitecture;
            public ushort wReserved;
            public uint dwPageSize;
            public IntPtr lpMinimumApplicationAddress;
            public IntPtr lpMaximumApplicationAddress;
            public IntPtr dwActiveProcessorMask;
            public uint dwNumberOfProcessors;
            public uint dwProcessorType;
            public uint dwAllocationGranularity;
            public ushort wProcessorLevel;
            public ushort wProcessorRevision;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

        [DllImport("dxgi.dll", CallingConvention = CallingConvention.StdCall)]
        static extern int CreateDXGIFactory1(ref Guid riid, out IDXGIFactory1 ppFactory);

        static readonly Guid IID_IDXGIFactory1 = new Guid("770aae78-f4f8-4cfb-8f6c-3c3e2e6e3e2e");

        [StructLayout(LayoutKind.Sequential)]
        struct MEMORYSTATUSEX
        {
            public uint dwLength;
            public uint dwMemoryLoad;
            public ulong ullTotalPhys;
            public ulong ullAvailPhys;
            public ulong ullTotalPageFile;
            public ulong ullAvailPageFile;
            public ulong ullTotalVirtual;
            public ulong ullAvailVirtual;
            public ulong ullAvailExtendedVirtual;
        }

        [DllImport("user32.dll")]
        static extern bool EnumDisplayDevices(string lpDevice, uint iDevNum, ref DISPLAY_DEVICE lpDisplayDevice, uint dwFlags);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
        struct DISPLAY_DEVICE
        {
            public int cb;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string DeviceName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string DeviceString;
            public uint StateFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string DeviceID;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string DeviceKey;
        }

        public class GPUInfo
        {
            public string Name { get; set; }
            public ulong DedicatedVRAM { get; set; }  // Dedicated GPU memory
            public ulong SharedVRAM { get; set; }     // Shared system memory
            public uint VendorId { get; set; }
            public uint DeviceId { get; set; }
            public bool IsNvidia { get { return VendorId == 0x10DE; } }
            public bool IsAMD { get { return VendorId == 0x1002 || VendorId == 0x1022; } }
            public bool IsIntel { get { return VendorId == 0x8086; } }
            public bool IsDiscrete { get { return DedicatedVRAM > 512 * 1024 * 1024; } } // > 512MB considered discrete
        }

        public class HardwareInfo
        {
            public ulong TotalRAM { get; set; }  // in bytes
            public int CpuCores { get; set; }
            public bool HasCudaGPU { get; set; }
            public ulong GpuVram { get; set; }  // Primary GPU VRAM (for backwards compat)
            public string GpuName { get; set; }  // Primary GPU name (for backwards compat)
            public List<GPUInfo> GPUs { get; set; }  // All detected GPUs
            public GPUInfo BestGPU { get; set; }  // Best GPU for compute

            public HardwareInfo()
            {
                GPUs = new List<GPUInfo>();
            }
        }

        /// <summary>
        /// Get accurate VRAM information using DXGI
        /// </summary>
        public static List<GPUInfo> GetGPUInfo()
        {
            var gpus = new List<GPUInfo>();

            try
            {
                IDXGIFactory1 factory = null;
                Guid iid = IID_IDXGIFactory1;
                int hr = CreateDXGIFactory1(ref iid, out factory);

                if (hr != 0 || factory == null)
                    throw new Exception("DXGI failed");

                uint adapterIndex = 0;
                while (true)
                {
                    try
                    {
                        IDXGIAdapter1 adapter = null;
                        factory.EnumAdapters1(adapterIndex, out adapter);

                        if (adapter == null)
                            break;

                        DXGI_ADAPTER_DESC desc;
                        adapter.GetDesc(out desc);

                        // Skip software adapters (Basic Render Driver)
                        if (desc.VendorId == 0x1414 && desc.DeviceId == 0x008C)
                        {
                            adapterIndex++;
                            continue;
                        }

                        var gpu = new GPUInfo
                        {
                            Name = GetAdapterName(desc.VendorId, desc.DeviceId),
                            DedicatedVRAM = desc.DedicatedVideoMemory,
                            SharedVRAM = desc.SharedSystemMemory,
                            VendorId = desc.VendorId,
                            DeviceId = desc.DeviceId
                        };

                        gpus.Add(gpu);
                        adapterIndex++;
                    }
                    catch
                    {
                        break;
                    }
                }
            }
            catch { /* DXGI not available on older systems */ }

            // Fallback to WMI if DXGI fails
            if (gpus.Count == 0)
            {
                try
                {
                    using (var searcher = new ManagementObjectSearcher("SELECT * FROM Win32_VideoController"))
                    {
                        foreach (ManagementObject obj in searcher.Get())
                        {
                            object nameObj = obj["Name"];
                            string name = nameObj != null ? nameObj.ToString() : "Unknown GPU";

                            ulong vram = 0;
                            try
                            {
                                var adapterRam = obj["AdapterRAM"];
                                if (adapterRam != null)
                                    vram = Convert.ToUInt64(adapterRam);
                            }
                            catch { }

                            gpus.Add(new GPUInfo
                            {
                                Name = name,
                                DedicatedVRAM = vram,
                                VendorId = GetVendorIdFromName(name)
                            });
                        }
                    }
                }
                catch { }
            }

            // Fallback to EnumDisplayDevices if WMI fails
            if (gpus.Count == 0)
            {
                try
                {
                    DISPLAY_DEVICE d = new DISPLAY_DEVICE();
                    d.cb = Marshal.SizeOf(d);

                    uint i = 0;
                    var addedNames = new HashSet<string>();
                    while (EnumDisplayDevices(null, i, ref d, 0))
                    {
                        if (!string.IsNullOrEmpty(d.DeviceString) && !addedNames.Contains(d.DeviceString))
                        {
                            addedNames.Add(d.DeviceString);
                            ulong vram = GetVramFromRegistry(d.DeviceString);
                            uint vendorId = GetVendorIdFromName(d.DeviceString);
                            
                            // If registry failed and it's Nvidia or AMD, assume it's discrete and give it a dummy 8GB VRAM
                            if (vram == 0 && (vendorId == 0x10DE || vendorId == 0x1002))
                                vram = 8UL * 1024 * 1024 * 1024;

                            gpus.Add(new GPUInfo
                            {
                                Name = d.DeviceString,
                                DedicatedVRAM = vram,
                                VendorId = vendorId
                            });
                        }
                        i++;
                    }
                }
                catch { }
            }

            return gpus;
        }

        private static ulong GetVramFromRegistry(string driverDesc)
        {
            try
            {
                using (var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4D36E968-E325-11CE-BFC1-08002BE10318}"))
                {
                    if (key != null)
                    {
                        foreach (string subkeyName in key.GetSubKeyNames())
                        {
                            if (subkeyName.Length == 4)
                            {
                                using (var subkey = key.OpenSubKey(subkeyName))
                                {
                                    string desc = subkey.GetValue("DriverDesc") as string;
                                    if (desc == driverDesc)
                                    {
                                        object qwMemSizeObj = subkey.GetValue("HardwareInformation.qwMemorySize");
                                        if (qwMemSizeObj != null)
                                        {
                                            if (qwMemSizeObj is byte[])
                                            {
                                                byte[] b = (byte[])qwMemSizeObj;
                                                if (b.Length >= 8)
                                                    return BitConverter.ToUInt64(b, 0);
                                            }
                                            else
                                            {
                                                return unchecked((ulong)Convert.ToInt64(qwMemSizeObj));
                                            }
                                        }
                                        
                                        object memSizeObj = subkey.GetValue("HardwareInformation.MemorySize");
                                        if (memSizeObj != null)
                                        {
                                            if (memSizeObj is byte[])
                                            {
                                                byte[] b = (byte[])memSizeObj;
                                                if (b.Length >= 8)
                                                    return BitConverter.ToUInt64(b, 0);
                                                else if (b.Length >= 4)
                                                    return BitConverter.ToUInt32(b, 0);
                                            }
                                            else
                                            {
                                                ulong val = unchecked((ulong)Convert.ToInt64(memSizeObj));
                                                if (val < 0xFFFFFFFF00000000UL) 
                                                    return val;
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            }
            catch { }
            return 0;
        }

        private static uint GetVendorIdFromName(string name)
        {
            string lower = name.ToLower();
            if (lower.Contains("nvidia")) return 0x10DE;
            if (lower.Contains("amd") || lower.Contains("radeon")) return 0x1002;
            if (lower.Contains("intel")) return 0x8086;
            return 0;
        }

        private static string GetAdapterName(uint vendorId, uint deviceId)
        {
            // Common vendor detection
            switch (vendorId)
            {
                case 0x10DE: return "NVIDIA GPU (0x" + deviceId.ToString("X4") + ")";
                case 0x1002:
                case 0x1022: return "AMD GPU (0x" + deviceId.ToString("X4") + ")";
                case 0x8086: return "Intel GPU (0x" + deviceId.ToString("X4") + ")";
                default: return "GPU (Vendor: 0x" + vendorId.ToString("X4") + ")";
            }
        }

        public class ModelRecommendation
        {
            public string ModelId { get; set; }
            public string ModelName { get; set; }
            public string DisplayName { get; set; }
            public string HuggingFaceUrl { get; set; }
            public long ModelSizeBytes { get; set; }
            public int ContextLength { get; set; }
            public string Description { get; set; }
            public bool NeedsGpu { get; set; }
            public int MinRamGB { get; set; }
            public int MinVramGB { get; set; }
        }

        public static HardwareInfo GetHardwareInfo()
        {
            var info = new HardwareInfo();

            // Get RAM
            var memStatus = new MEMORYSTATUSEX();
            memStatus.dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX));
            if (GlobalMemoryStatusEx(ref memStatus))
            {
                info.TotalRAM = memStatus.ullTotalPhys;
            }

            // Get CPU cores
            var sysInfo = new SYSTEM_INFO();
            GetNativeSystemInfo(ref sysInfo);
            info.CpuCores = (int)sysInfo.dwNumberOfProcessors;

            // Get GPU info using DXGI (accurate VRAM detection)
            info.GPUs = GetGPUInfo();

            // Find best GPU for compute (prefer discrete NVIDIA/AMD)
            foreach (var gpu in info.GPUs)
            {
                if (info.BestGPU == null)
                {
                    info.BestGPU = gpu;
                }
                else if (gpu.IsDiscrete && !info.BestGPU.IsDiscrete)
                {
                    // Prefer discrete over integrated
                    info.BestGPU = gpu;
                }
                else if (gpu.IsDiscrete && info.BestGPU.IsDiscrete)
                {
                    // Both discrete, prefer more VRAM
                    if (gpu.DedicatedVRAM > info.BestGPU.DedicatedVRAM)
                        info.BestGPU = gpu;
                }
            }

            // Set legacy properties for backwards compatibility
            if (info.BestGPU != null)
            {
                info.GpuName = info.BestGPU.Name;
                info.GpuVram = info.BestGPU.DedicatedVRAM;
                info.HasCudaGPU = info.BestGPU.IsNvidia;
            }

            return info;
        }

        public static ModelRecommendation RecommendModel(HardwareInfo info = null)
        {
            if (info == null)
                info = GetHardwareInfo();

            long ramGB = (long)Math.Round(info.TotalRAM / (double)(1024 * 1024 * 1024));
            long vramGB = (long)Math.Round(info.GpuVram / (double)(1024 * 1024 * 1024));

            // Free models from HuggingFace (Qwen3, DeepSeek, etc.)
            // Using Qwen3 models which are Apache 2.0 licensed

            // High-end: 32GB+ RAM or 12GB+ VRAM
            if (ramGB >= 32 || vramGB >= 12)
            {
                return new ModelRecommendation
                {
                    ModelId = "qwen3-30b-a3b",
                    ModelName = "Qwen3-30B-A3B-Q4_K_M",
                    DisplayName = "Qwen3 30B (High Quality)",
                    HuggingFaceUrl = "https://huggingface.co/Qwen/Qwen3-30B-A3B-GGUF/resolve/main/Qwen3-30B-A3B-Q4_K_M.gguf",
                    ModelSizeBytes = 18L * 1024 * 1024 * 1024,  // ~18GB
                    ContextLength = 32768,
                    Description = "Best quality responses, requires high-end hardware",
                    NeedsGpu = vramGB >= 16,
                    MinRamGB = 32,
                    MinVramGB = 12
                };
            }

            // Mid-high: 16GB+ RAM or 8GB+ VRAM
            if (ramGB >= 16 || vramGB >= 8)
            {
                return new ModelRecommendation
                {
                    ModelId = "qwen3-14b",
                    ModelName = "Qwen3-14B-Q4_K_M",
                    DisplayName = "Qwen3 14B (Balanced)",
                    HuggingFaceUrl = "https://huggingface.co/Qwen/Qwen3-14B-GGUF/resolve/main/Qwen3-14B-Q4_K_M.gguf",
                    ModelSizeBytes = 9L * 1024 * 1024 * 1024,  // ~9GB
                    ContextLength = 32768,
                    Description = "Good balance of quality and speed",
                    NeedsGpu = vramGB >= 8,
                    MinRamGB = 16,
                    MinVramGB = 8
                };
            }

            // Mid: 8GB+ RAM
            // Mid: 8GB+ RAM
            if (ramGB >= 8)
            {
                return new ModelRecommendation
                {
                    ModelId = "qwen3-8b",
                    ModelName = "Qwen3-8B-Q4_K_M",
                    DisplayName = "Qwen3 8B (Fast)",
                    HuggingFaceUrl = "https://huggingface.co/Qwen/Qwen3-8B-GGUF/resolve/main/Qwen3-8B-Q4_K_M.gguf",
                    ModelSizeBytes = 5L * 1024 * 1024 * 1024,
                    ContextLength = 32768,
                    Description = "Fast responses, good for most coding tasks",
                    NeedsGpu = false,
                    MinRamGB = 8,
                    MinVramGB = 0
                };
            }

            // Low: 4GB+ RAM
            return new ModelRecommendation
            {
                ModelId = "qwen3-4b",
                ModelName = "Qwen3-4B-Q4_K_M",
                DisplayName = "Qwen3 4B (Lightweight)",
                HuggingFaceUrl = "https://huggingface.co/Qwen/Qwen3-4B-GGUF/resolve/main/Qwen3-4B-Q4_K_M.gguf",
                ModelSizeBytes = 2L * 1024 * 1024 * 1024,
                ContextLength = 32768,
                Description = "Lightweight, works on most systems",
                NeedsGpu = false,
                MinRamGB = 4,
                MinVramGB = 0
            };
        }

        public static void PrintHardwareInfo()
        {
            var info = GetHardwareInfo();
            long ramGB = (long)Math.Round(info.TotalRAM / (double)(1024 * 1024 * 1024));
            long vramGB = info.BestGPU != null ? (long)Math.Round(info.BestGPU.DedicatedVRAM / (double)(1024 * 1024 * 1024)) : 0;

            Console.WriteLine("  Hardware detected:");
            Console.WriteLine("    RAM: " + ramGB + " GB");
            Console.WriteLine("    CPU cores: " + info.CpuCores);
            if (!string.IsNullOrEmpty(info.GpuName))
            {
                Console.WriteLine("    GPU: " + info.GpuName);
                if (vramGB > 0)
                    Console.WriteLine("    GPU VRAM: " + vramGB + " GB");
            }
            Console.WriteLine();

            var rec = RecommendModel(info);
            Console.WriteLine("  Recommended model: " + rec.DisplayName);
            Console.WriteLine("  " + rec.Description);
            Console.WriteLine("  Context length: " + rec.ContextLength + " tokens");
        }
    }
}
