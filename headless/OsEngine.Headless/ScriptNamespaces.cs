// Namespaces that robot scripts in Custom/Robots import but the Linux build lacks (System.Drawing.Common is
// Windows-only). 28 stock scripts carry an unused `using System.Drawing.Drawing2D;` and failed to compile on
// the VPS, which made wiki_robots_list re-compile them on every call (~5 s). A public type in the namespace
// makes it visible to the runtime script compiler (BotFactory references this assembly), so the using resolves.
namespace System.Drawing.Drawing2D
{
    public static class __ScriptNamespaceAnchor
    {
    }
}
