namespace Xilium.CefGlue.Interop;

internal struct cef_version_info_t
{
    public nuint size;

    public int cef_version_major;
    public int cef_version_minor;
    public int cef_version_patch;
    public int cef_commit_number;
    public int chrome_version_major;
    public int chrome_version_minor;
    public int chrome_version_build;
    public int chrome_version_patch;
}
