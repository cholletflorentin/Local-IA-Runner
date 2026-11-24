using Android.App;
using Android.Content.PM;
using Android.OS;
using Android.Views;

namespace LocalIARunner.Uno.Droid;

[Activity(
    MainLauncher = true,
    ConfigurationChanges = Uno.UI.ActivityHelper.AllConfigChanges,
    WindowSoftInputMode = SoftInput.AdjustNothing | SoftInput.StateHidden,
    Theme = "@style/AppTheme",
    Exported = true)]
public sealed class MainActivity : Uno.UI.Runtime.Android.ApplicationActivity
{
    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
    }
}
