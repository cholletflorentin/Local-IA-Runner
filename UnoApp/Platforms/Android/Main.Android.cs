using Android.App;
using Android.OS;
using Android.Runtime;
using Uno.UI.Hosting;

namespace LocalIARunner.Uno.Droid;

[Application(Label = "Local IA Runner", Theme = "@style/AppTheme", LargeHeap = true, HardwareAccelerated = true)]
public sealed class Application : global::Microsoft.UI.Xaml.NativeApplication
{
    public Application(IntPtr javaReference, JniHandleOwnership transfer)
        : base(() => new global::LocalIARunner.Uno.App(), javaReference, transfer)
    {
    }
}
