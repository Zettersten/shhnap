using System.Runtime.InteropServices;
using Shnapp.Core;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Streams;

namespace Shnapp.App;

internal sealed partial class AppController
{
    private static readonly Guid DataTransferManagerId =
        new(0xa5caee9b, 0x8708, 0x49d1, 0x8d, 0x36, 0x67, 0xd2, 0x5a, 0x8d, 0xa0, 0x0c);

    private IDataTransferManagerInterop? _shareInterop;
    private DataTransferManager? _shareManager;
    private SharePayload? _pendingShare;

    internal void ShareCurrentDocument() => Run(async () =>
    {
        if (_page.Document is { } document)
        {
            await ShareDocumentAsync(document.Id);
        }
    });

    internal void ShareDocument(Guid id) => Run(() => ShareDocumentAsync(id));

    private async Task ShareDocumentAsync(Guid id)
    {
        if (!await _captureGate.WaitAsync(0, _lifetime.Token))
        {
            return;
        }

        try
        {
            if (_page.Document?.Id == id)
            {
                _page.CommitText();
                _saveTimer.Stop();
                await SaveCurrentAsync();
            }

            string title;
            string snapshotPath;
            string thumbnailSnapshotPath;
            await _saveGate.WaitAsync(_lifetime.Token);
            try
            {
                await RecoverPendingSaveUnderGateAsync(id);
                ShnappDocument document = await Library.OpenAsync(id, _lifetime.Token)
                    ?? throw new FileNotFoundException("This shnapp is no longer in your Library.");
                EnsureLibraryImagesSafe(id, create: false);
                title = document.Title;
                snapshotPath = await _exportSnapshots.CreateAsync(id,
                    Library.GetExportPath(id), _lifetime.Token);
                thumbnailSnapshotPath = await _exportSnapshots.CreateThumbnailAsync(id,
                    Library.GetGalleryPreviewPath(id), _lifetime.Token);
            }
            finally
            {
                _saveGate.Release();
            }

            // Windows may read the share payload after this method returns. The
            // snapshot keeps its image fixed while later edits replace shnapp.png.
            StorageFile image = await StorageFile.GetFileFromPathAsync(snapshotPath);
            StorageFile thumbnail = await StorageFile.GetFileFromPathAsync(thumbnailSnapshotPath);

            EnsureShareManager();
            _pendingShare = new SharePayload(title, image, thumbnail);
            try
            {
                _shareInterop!.ShowShareUIForWindow(App.WindowHandle);
                _page.ViewModel.Status = "Choose where to share this shnapp";
            }
            catch
            {
                _pendingShare = null;
                throw;
            }
        }
        finally
        {
            _captureGate.Release();
        }
    }

    private void EnsureShareManager()
    {
        if (_shareManager is not null)
        {
            return;
        }

        // Desktop WinUI uses per-window COM interop; GetForCurrentView is UWP-only.
        _shareInterop = DataTransferManager.As<IDataTransferManagerInterop>();
        Guid id = DataTransferManagerId;
        nint abi = _shareInterop.GetForWindow(App.WindowHandle, ref id);
        try
        {
            _shareManager = WinRT.MarshalInterface<DataTransferManager>.FromAbi(abi);
        }
        finally
        {
            Marshal.Release(abi);
        }
        _shareManager.DataRequested += ShareDataRequested;
    }

    private void ShareDataRequested(DataTransferManager sender, DataRequestedEventArgs args)
    {
        SharePayload? payload = _pendingShare;
        _pendingShare = null;
        if (payload is null)
        {
            args.Request.FailWithDisplayText("Choose a shnapp before sharing.");
            return;
        }

        DataPackage data = args.Request.Data;
        data.Properties.Title = payload.Title;
        data.Properties.Description = "Shnapp PNG image";
        data.RequestedOperation = DataPackageOperation.Copy;
        data.SetStorageItems([payload.Image], readOnly: true);
        data.SetBitmap(RandomAccessStreamReference.CreateFromFile(payload.Image));
        data.Properties.Thumbnail = RandomAccessStreamReference.CreateFromFile(payload.Thumbnail);
    }

    private sealed record SharePayload(string Title, StorageFile Image, StorageFile Thumbnail);

    [ComImport]
    [Guid("3A3DCD6C-3EAB-43DC-BCDE-45671CE800C8")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDataTransferManagerInterop
    {
        nint GetForWindow([In] nint appWindow, [In] ref Guid riid);
        void ShowShareUIForWindow(nint appWindow);
    }
}
