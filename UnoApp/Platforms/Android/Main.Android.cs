using Android.App;
using Android.OS;
using Android.Runtime;
using Uno.UI.Hosting;

namespace LocalIARunner.Uno.Droid;

[Application(Label = "Local IA Runner", Theme = "@style/AppTheme", LargeHeap = true, HardwareAccelerated = true)]
public sealed class Application : global::Uno.UI.Runtime.Android.NativeApplication
{
    public Application(IntPtr javaReference, JniHandleOwnership transfer)
        : base(javaReference, transfer)
    {
    }

    protected override global::Uno.UI.Hosting.UnoPlatformHost CreateHost() =>
        UnoPlatformHostBuilder.Create()
            .App(() => new LocalIARunner.Uno.App())
            .UseAndroid()
            .Build();
}
