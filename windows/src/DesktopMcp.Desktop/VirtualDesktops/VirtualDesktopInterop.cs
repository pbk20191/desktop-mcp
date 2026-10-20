using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace DesktopMcp.Desktop.VirtualDesktops;

// The undocumented shell interfaces upstream uses for desktop names (vdm/core.py:67-367), declared by hand because they
// are not in the Windows SDK metadata (spec §6). Only the slots up to GetDesktops are called; the earlier ones are
// declared so the vtable order is right. Their IIDs change between Windows builds, so the manager has one declaration
// per supported build. IVirtualDesktop is the same on 22621 and 26100. A build whose IIDs differ fails open: QueryService
// or the wrapper's QueryInterface fails with E_NOINTERFACE before any slot is called, and the service falls back to
// "Default Desktop".

internal static class VirtualDesktopIds
{
    public static readonly Guid ImmersiveShell = new("C2F03A33-21F5-47FA-B4BB-156362A2F239");
    public static readonly Guid VirtualDesktopManagerInternalService = new("C5E0CDCA-7B6E-41B2-9FC4-D93975CC467B");

    /// <summary>upstream vdm/core.py:137-148; Windows 10 (19041) is not supported (spec §2).</summary>
    public static Guid ManagerInternal(int build) => build >= 26100
        ? typeof(IVirtualDesktopManagerInternal26100).GUID
        : typeof(IVirtualDesktopManagerInternal22621).GUID;
}

[GeneratedComInterface, Guid("A3175F2D-239C-4BD2-8AA0-EEBA8B0B138E")]
internal unsafe partial interface IVirtualDesktopManagerInternal22621
{
    [PreserveSig] int GetCount(out uint count);
    [PreserveSig] int MoveViewToDesktop(void* view, void* desktop);
    [PreserveSig] int CanViewMoveDesktops(void* view, int* canMove);
    [PreserveSig] int GetCurrentDesktop(out void* desktop);
    [PreserveSig] int GetDesktops(out void* desktops);
}

[GeneratedComInterface, Guid("53F5CA0B-158F-4124-900C-057158060B27")]
internal unsafe partial interface IVirtualDesktopManagerInternal26100
{
    [PreserveSig] int GetCount(out uint count);
    [PreserveSig] int MoveViewToDesktop(void* view, void* desktop);
    [PreserveSig] int CanViewMoveDesktops(void* view, int* canMove);
    [PreserveSig] int GetCurrentDesktop(out void* desktop);
    [PreserveSig] int GetDesktops(out void* desktops);
}

[GeneratedComInterface, Guid("3F07F4BE-B107-441A-AF0F-39D82529072C")]
internal unsafe partial interface IVirtualDesktop
{
    [PreserveSig] int IsViewVisible(void* view, int* visible);
    [PreserveSig] int GetID(out Guid id);
}
