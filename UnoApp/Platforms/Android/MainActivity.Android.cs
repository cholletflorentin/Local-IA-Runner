using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Android.Views;
using AndroidUri = Android.Net.Uri;

namespace LocalIARunner.Uno.Droid;

[Activity(
    MainLauncher = true,
    ConfigurationChanges = global::Uno.UI.ActivityHelper.AllConfigChanges,
    WindowSoftInputMode = SoftInput.AdjustNothing | SoftInput.StateHidden,
    Theme = "@style/AppTheme",
    Exported = true)]
public sealed class MainActivity : global::Microsoft.UI.Xaml.ApplicationActivity
{
    private const int PickModelRequestCode = 7412;
    private static TaskCompletionSource<AndroidUri?>? _pendingModelPick;

    public static MainActivity? Current { get; private set; }

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        Current = this;
    }

    protected override void OnResume()
    {
        base.OnResume();
        Current = this;
    }

    public static Task<AndroidUri?> PickModelAsync()
    {
        var activity = Current ?? throw new InvalidOperationException("La fenêtre Android n'est pas prête.");
        if (_pendingModelPick is not null)
            throw new InvalidOperationException("Un sélecteur de fichier est déjà ouvert.");

        var completion = new TaskCompletionSource<AndroidUri?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingModelPick = completion;
        var intent = new Intent(Intent.ActionOpenDocument);
        intent.AddCategory(Intent.CategoryOpenable);
        intent.SetType("*/*");
        intent.PutExtra(Intent.ExtraMimeTypes, new[] { "application/octet-stream", "application/x-gguf" });
        intent.AddFlags(ActivityFlags.GrantReadUriPermission);
        try
        {
            activity.StartActivityForResult(intent, PickModelRequestCode);
        }
        catch
        {
            _pendingModelPick = null;
            throw;
        }
        return completion.Task;
    }

    protected override void OnActivityResult(int requestCode, Result resultCode, Intent? data)
    {
        base.OnActivityResult(requestCode, resultCode, data);
        if (requestCode != PickModelRequestCode) return;

        var completion = _pendingModelPick;
        _pendingModelPick = null;
        completion?.TrySetResult(resultCode == Result.Ok ? data?.Data : null);
    }

    protected override void OnDestroy()
    {
        if (ReferenceEquals(Current, this))
            Current = null;
        _pendingModelPick?.TrySetResult(null);
        _pendingModelPick = null;
        AndroidLlamaEngine.Stop();
        base.OnDestroy();
    }
}
