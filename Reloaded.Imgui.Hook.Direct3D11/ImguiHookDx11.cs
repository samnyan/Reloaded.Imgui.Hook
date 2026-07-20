using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using DearImguiSharp;
using Reloaded.Hooks.Definitions;
using Reloaded.Imgui.Hook.DirectX.Definitions;
using Reloaded.Imgui.Hook.DirectX.Hooks;
using Reloaded.Imgui.Hook.Implementations;
using Reloaded.Imgui.Hook.Misc;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using static Reloaded.Imgui.Hook.Misc.Native;
using Device = SharpDX.Direct3D11.Device;

namespace Reloaded.Imgui.Hook.Direct3D11
{
    public unsafe class ImguiHookDx11 : IImguiHook
    {
        public static ImguiHookDx11 Instance { get; private set; }

        private IHook<DX11Hook.Present> _presentHook;
        private IntPtr[] _swapChainVTable;
        private GCHandle _swapChainVTableHandle;
        private IntPtr _originalSwapChainVTable;
        private IntPtr _originalResizeBuffers;
        private IntPtr _hookedSwapChainPtr;
        private bool _initialized;
        private RenderTargetView _renderTargetView;
        private readonly object _renderGate = new object();

        private static readonly string[] _supportedDlls = new string[]
        {
            "d3d11.dll",
            "d3d11_1.dll",
            "d3d11_2.dll",
            "d3d11_3.dll",
            "d3d11_4.dll"
        };

        // Dear ImGui may re-enter graphics APIs internally. Keep recursion guards
        // thread-local so unrelated render and resize threads still serialize.
        [ThreadStatic]
        private static bool _presentRecursionLock;

        [ThreadStatic]
        private static bool _resizeRecursionLock;

        public ImguiHookDx11() { }

        public bool IsApiSupported()
        {
            foreach (var dll in _supportedDlls)
            {
                if (GetModuleHandle(dll) != IntPtr.Zero)
                    return true;
            }

            return false;
        }

        public void Initialize()
        {
            var presentPtr = (long)DX11Hook.DXGIVTable[(int)IDXGISwapChain.Present].FunctionPointer;
            Instance = this;
            _presentHook = SDK.Hooks.CreateHook<DX11Hook.Present>(typeof(ImguiHookDx11), nameof(PresentImplStatic), presentPtr).Activate();

        }
        ~ImguiHookDx11()
        {
            ReleaseUnmanagedResources();
        }

        public void Dispose()
        {
            ReleaseUnmanagedResources();
            GC.SuppressFinalize(this);
        }

        private void ReleaseUnmanagedResources()
        {
            lock (_renderGate)
            {
                _renderTargetView?.Dispose();
                _renderTargetView = null;

                if (_initialized)
                {
                    Debug.WriteLine($"[DX11 Dispose] Shutdown");
                    ImGui.SetCurrentContext(ImguiHook.Context);
                    ImGui.ImGuiImplDX11Shutdown();
                    _initialized = false;
                }
            }
        }

        private int ResizeBuffersImpl(IntPtr swapchainPtr, uint bufferCount, uint width, uint height, Format newFormat, uint swapchainFlags)
        {
            if (_originalResizeBuffers == IntPtr.Zero || _hookedSwapChainPtr != swapchainPtr)
                return unchecked((int)0x80004005);

            if (_resizeRecursionLock)
            {
                Debug.WriteLine($"[DX11 ResizeBuffers] Discarding via Recursion Lock");
                return CallOriginalResizeBuffers(swapchainPtr, bufferCount, width, height, newFormat, swapchainFlags);
            }

            _resizeRecursionLock = true;
            try
            {
                var swapChain = new SwapChain(swapchainPtr);
                var windowHandle = swapChain.Description.OutputHandle;
                Debug.DebugWriteLine($"[DX11 ResizeBuffers] Window Handle {windowHandle}");

                // Ignore windows which don't belong to us.
                if (!ImguiHook.CheckWindowHandle(windowHandle))
                {
                    Debug.WriteLine($"[DX11 ResizeBuffers] Discarding Window Handle {windowHandle} due to Mismatch");
                    return CallOriginalResizeBuffers(swapchainPtr, bufferCount, width, height, newFormat, swapchainFlags);
                }

                lock (_renderGate)
                {
                    ReleaseRenderTarget();
                    var result = CallOriginalResizeBuffers(swapchainPtr, bufferCount, width, height, newFormat, swapchainFlags);
                    CreateRenderTarget(swapchainPtr);

                    if (result < 0)
                        Debug.WriteLine($"[DX11 ResizeBuffers] Original failed with HRESULT 0x{unchecked((uint)result):X8}");

                    return result;
                }
            }
            finally
            {
                _resizeRecursionLock = false;
            }
        }

        private void ReleaseRenderTarget()
        {
            _renderTargetView?.Dispose();
            _renderTargetView = null;
        }

        private void CreateRenderTarget(IntPtr swapChainPtr)
        {
            if (_initialized)
            {
                var swapChain = new SwapChain(swapChainPtr);
                using var device = swapChain.GetDevice<Device>();
                using var backBuffer = swapChain.GetBackBuffer<Texture2D>(0);
                _renderTargetView = new RenderTargetView(device, backBuffer);
            }
        }

        private unsafe int PresentImpl(IntPtr swapChainPtr, int syncInterval, PresentFlags flags)
        {
            if (_presentRecursionLock)
            {
                Debug.WriteLine($"[DX11 Present] Discarding via Recursion Lock");
                return _presentHook.OriginalFunction.Value.Invoke(swapChainPtr, syncInterval, flags);
            }

            _presentRecursionLock = true;
            try
            {
                var swapChain = new SwapChain(swapChainPtr);
                var windowHandle = swapChain.Description.OutputHandle;

                // Ignore windows which don't belong to us.
                if (!ImguiHook.CheckWindowHandle(windowHandle))
                {
                    Debug.WriteLine($"[DX11 Present] Discarding Window Handle {windowHandle} due to Mismatch");
                    return _presentHook.OriginalFunction.Value.Invoke(swapChainPtr, syncInterval, flags);
                }

                lock (_renderGate)
                {
                    EnsureResizeBuffersHook(swapChainPtr);
                    ImGui.SetCurrentContext(ImguiHook.Context);
                    using var device = swapChain.GetDevice<Device>();
                    using var deviceContext = device.ImmediateContext;
                    var backendReady = true;
                    if (!_initialized)
                    {
                        Debug.WriteLine($"[DX11 Present] Init DX11, Window Handle: {windowHandle:X}");
                        ImguiHook.InitializeWithHandle(windowHandle);
                        if (!ImGui.ImGuiImplDX11Init((void*)device.NativePointer, (void*)deviceContext.NativePointer))
                        {
                            Debug.WriteLine($"[DX11 Present] DX11 backend initialization failed");
                            backendReady = false;
                        }
                        else
                        {
                            using var backBuffer = swapChain.GetBackBuffer<Texture2D>(0);
                            _renderTargetView = new RenderTargetView(device, backBuffer);
                            _initialized = true;
                        }
                    }

                    if (backendReady)
                    {
                        ImGui.ImGuiImplDX11NewFrame();
                        ImguiHook.NewFrame();
                        deviceContext.OutputMerger.SetRenderTargets(_renderTargetView);
                        using var drawData = ImGui.GetDrawData();
                        ImGui.ImGuiImplDX11RenderDrawData(drawData);
                    }
                }

                return _presentHook.OriginalFunction.Value.Invoke(swapChainPtr, syncInterval, flags);
            }
            finally
            {
                _presentRecursionLock = false;
            }
        }

        private unsafe int CallOriginalResizeBuffers(IntPtr swapchainPtr, uint bufferCount, uint width, uint height, Format newFormat, uint swapchainFlags)
        {
            var original = (delegate* unmanaged[Stdcall]<IntPtr, uint, uint, uint, Format, uint, int>)_originalResizeBuffers;
            return original(swapchainPtr, bufferCount, width, height, newFormat, swapchainFlags);
        }

        private void EnsureResizeBuffersHook(IntPtr swapChainPtr)
        {
            if (_swapChainVTableHandle.IsAllocated && _hookedSwapChainPtr == swapChainPtr)
                return;

            RestoreResizeBuffersVTable();

            // Hook only this swap chain's vtable entry. An inline hook on the shared DXGI
            // implementation produces an invalid trampoline for ResizeBuffers on some systems.
            var methodCount = Enum.GetNames(typeof(IDXGISwapChain)).Length;
            _originalSwapChainVTable = Marshal.ReadIntPtr(swapChainPtr);
            _swapChainVTable = new IntPtr[methodCount];
            for (var index = 0; index < methodCount; index++)
                _swapChainVTable[index] = Marshal.ReadIntPtr(_originalSwapChainVTable, index * IntPtr.Size);

            _originalResizeBuffers = _swapChainVTable[(int)IDXGISwapChain.ResizeBuffers];
            _swapChainVTable[(int)IDXGISwapChain.ResizeBuffers] = (IntPtr)SDK.Hooks.Utilities.GetFunctionPointer(
                typeof(ImguiHookDx11),
                nameof(ResizeBuffersImplStatic));
            _swapChainVTableHandle = GCHandle.Alloc(_swapChainVTable, GCHandleType.Pinned);
            _hookedSwapChainPtr = swapChainPtr;
            Marshal.WriteIntPtr(swapChainPtr, _swapChainVTableHandle.AddrOfPinnedObject());

            Debug.WriteLine($"[DX11 Hook] Resize vtable installed for 0x{swapChainPtr.ToInt64():X}, original 0x{_originalResizeBuffers.ToInt64():X}, hook 0x{_swapChainVTable[(int)IDXGISwapChain.ResizeBuffers].ToInt64():X}");
        }

        private void RestoreResizeBuffersVTable()
        {
            if (_hookedSwapChainPtr != IntPtr.Zero &&
                _originalSwapChainVTable != IntPtr.Zero &&
                _swapChainVTableHandle.IsAllocated &&
                Marshal.ReadIntPtr(_hookedSwapChainPtr) == _swapChainVTableHandle.AddrOfPinnedObject())
            {
                Marshal.WriteIntPtr(_hookedSwapChainPtr, _originalSwapChainVTable);
            }

            if (_swapChainVTableHandle.IsAllocated)
                _swapChainVTableHandle.Free();

            _swapChainVTable = null;
            _originalSwapChainVTable = IntPtr.Zero;
            _originalResizeBuffers = IntPtr.Zero;
            _hookedSwapChainPtr = IntPtr.Zero;
        }

        public void Disable()
        {
            _presentHook?.Disable();
            lock (_renderGate)
            {
                RestoreResizeBuffersVTable();
            }
        }

        public void Enable()
        {
            _presentHook?.Enable();
        }

        #region Hook Functions
        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
        private static int ResizeBuffersImplStatic(IntPtr swapchainPtr, uint bufferCount, uint width, uint height, Format newFormat, uint swapchainFlags) => Instance.ResizeBuffersImpl(swapchainPtr, bufferCount, width, height, newFormat, swapchainFlags);

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
        private static int PresentImplStatic(IntPtr swapChainPtr, int syncInterval, PresentFlags flags) => Instance.PresentImpl(swapChainPtr, syncInterval, flags);
        #endregion
    }
}
