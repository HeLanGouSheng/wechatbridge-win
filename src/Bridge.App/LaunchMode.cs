using System.IO;
using System.Runtime.InteropServices;
using Bridge.App.Identity;
using Windows.ApplicationModel;
using Windows.ApplicationModel.Activation;
using Windows.ApplicationModel.DataTransfer.ShareTarget;
using WinRT;

namespace Bridge.App;

public abstract record LaunchMode
{
    /// <summary>WeChat (or any app) picked 微信桥 in the Windows share list.</summary>
    public sealed record Share(ShareOperation Operation) : LaunchMode;

    /// <summary>A .zip given on the command line or dropped on the exe: the same pipeline without WeChat.</summary>
    public sealed record LocalZip(string Path) : LaunchMode;

    public sealed record Window : LaunchMode;

    public static LaunchMode Detect(string[] args)
    {
        // GetActivatedEventArgs only means something for a process that runs with package identity;
        // without identity it throws or returns null.
        if (PackageIdentity.HasIdentity())
        {
            try
            {
                var activation = AppInstance.GetActivatedEventArgs();
                if (activation is not null && activation.Kind == ActivationKind.ShareTarget)
                {
                    return new Share(activation.As<ShareTargetActivatedEventArgs>().ShareOperation);
                }
            }
            catch (COMException)
            {
                // Not launched through an activation contract; fall through to a normal launch.
            }
        }

        if (args.Length > 0 && args[0].EndsWith(".zip", StringComparison.OrdinalIgnoreCase) && File.Exists(args[0]))
        {
            return new LocalZip(Path.GetFullPath(args[0]));
        }

        return new Window();
    }
}
