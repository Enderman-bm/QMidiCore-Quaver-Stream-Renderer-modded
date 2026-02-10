using System;
using System.Linq;
using Silk.NET.OpenCL;

namespace QQS_UI.Core
{
    /// <summary>
    /// OpenCL上下文管理器，负责初始化OpenCL平台和设�?
    /// </summary>
    public unsafe class OpenCLContext : IDisposable
    {
        public static bool IsAvailable { get; private set; }
        public static string? LastError { get; private set; }
        
        public static CL CLApi { get; private set; } = null!;
        
        public nint Platform { get; private set; }
        public nint Device { get; private set; }
        public nint Context { get; private set; }
        public nint CommandQueue { get; private set; }
        
        private bool disposed;

        static OpenCLContext()
        {
            try
            {
                CLApi = CL.GetApi();
                // 检查OpenCL是否可用
                uint platformCount = 0;
                int err = CLApi.GetPlatformIDs(0, null, &platformCount);
                IsAvailable = (err == (int)ErrorCodes.Success && platformCount > 0);
            }
            catch
            {
                IsAvailable = false;
            }
        }

        public OpenCLContext()
        {
            if (!IsAvailable)
            {
                throw new InvalidOperationException("OpenCL不可用，没有可用的GPU设备");
            }

            Initialize();
        }

        private void Initialize()
        {
            // 获取平台
            uint platformCount = 0;
            int err = CLApi.GetPlatformIDs(0, null, &platformCount);
            if (err != (int)ErrorCodes.Success || platformCount == 0)
            {
                throw new InvalidOperationException($"无法获取OpenCL平台: {err}");
            }

            nint[] platforms = new nint[platformCount];
            fixed (nint* pPlatforms = platforms)
            {
                err = CLApi.GetPlatformIDs(platformCount, pPlatforms, null);
            }
            
            // 选择第一个平�?
            Platform = platforms[0];

            // 获取GPU设备
            uint deviceCount = 0;
            err = CLApi.GetDeviceIDs(Platform, DeviceType.Gpu, 0, null, &deviceCount);
            if (err != (int)ErrorCodes.Success || deviceCount == 0)
            {
                // 如果没有GPU，尝试获取所有设�?
                err = CLApi.GetDeviceIDs(Platform, DeviceType.All, 0, null, &deviceCount);
                if (err != (int)ErrorCodes.Success || deviceCount == 0)
                {
                    throw new InvalidOperationException($"无法获取OpenCL设备: {err}");
                }
            }

            nint[] devices = new nint[deviceCount];
            fixed (nint* pDevices = devices)
            {
                err = CLApi.GetDeviceIDs(Platform, DeviceType.Gpu, deviceCount, pDevices, null);
                if (err != (int)ErrorCodes.Success)
                {
                    err = CLApi.GetDeviceIDs(Platform, DeviceType.All, deviceCount, pDevices, null);
                }
            }
            
            Device = devices[0];

            // 创建上下�?
            nint[] properties = new nint[] { (nint)ContextProperties.Platform, Platform, 0 };
            fixed (nint* pProperties = properties)
            {
                nint device = Device; Context = CLApi.CreateContext(pProperties, 1, &device, null, null, &err);
            }
            
            if (err != (int)ErrorCodes.Success)
            {
                throw new InvalidOperationException($"无法创建OpenCL上下�? {err}");
            }

            // 创建命令队列
            CommandQueue = CLApi.CreateCommandQueue(Context, Device, (CommandQueueProperties)0, &err);
            
            if (err != (int)ErrorCodes.Success)
            {
                throw new InvalidOperationException($"无法创建OpenCL命令队列: {err}");
            }
        }

        public string GetDeviceInfo()
        {
            if (Device == 0) return "未初始化";
            
            try
            {
                nuint size = 0;
                CLApi.GetDeviceInfo(Device, DeviceInfo.Name, 0, null, &size);
                byte[] nameBytes = new byte[size];
                fixed (byte* pName = nameBytes)
                {
                    CLApi.GetDeviceInfo(Device, DeviceInfo.Name, size, pName, null);
                }
                return System.Text.Encoding.UTF8.GetString(nameBytes).TrimEnd('\0');
            }
            catch
            {
                return "未知设备";
            }
        }

        public void Dispose()
        {
            if (!disposed)
            {
                if (CommandQueue != 0)
                {
                    CLApi.ReleaseCommandQueue(CommandQueue);
                    CommandQueue = 0;
                }
                if (Context != 0)
                {
                    CLApi.ReleaseContext(Context);
                    Context = 0;
                }
                disposed = true;
            }
            GC.SuppressFinalize(this);
        }

        ~OpenCLContext()
        {
            Dispose();
        }
    }
}
