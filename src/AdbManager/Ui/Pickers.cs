using Windows.Storage;
using Windows.Storage.Pickers;

namespace AdbManager.Ui;

/// <summary>文件 / 文件夹选择器的 WinUI 3 封装（需要窗口句柄初始化）。</summary>
public static class Pickers
{
    public static async Task<StorageFile?> PickFileAsync(params string[] extensions)
    {
        var picker = new FileOpenPicker
        {
            ViewMode = PickerViewMode.List,
            SuggestedStartLocation = PickerLocationId.Downloads
        };
        foreach (var extension in extensions) picker.FileTypeFilter.Add(extension);

        WinRT.Interop.InitializeWithWindow.Initialize(picker, App.WindowHandle);
        return await picker.PickSingleFileAsync();
    }

    public static async Task<IReadOnlyList<StorageFile>> PickFilesAsync(params string[] extensions)
    {
        var picker = new FileOpenPicker
        {
            ViewMode = PickerViewMode.List,
            SuggestedStartLocation = PickerLocationId.Downloads
        };
        foreach (var extension in extensions) picker.FileTypeFilter.Add(extension);

        WinRT.Interop.InitializeWithWindow.Initialize(picker, App.WindowHandle);
        return await picker.PickMultipleFilesAsync();
    }

    public static async Task<StorageFolder?> PickFolderAsync()
    {
        var picker = new FolderPicker
        {
            ViewMode = PickerViewMode.List,
            SuggestedStartLocation = PickerLocationId.Downloads
        };
        picker.FileTypeFilter.Add("*");

        WinRT.Interop.InitializeWithWindow.Initialize(picker, App.WindowHandle);
        return await picker.PickSingleFolderAsync();
    }

    public static async Task<StorageFile?> PickSaveFileAsync(string suggestedName, string extension, string displayName)
    {
        var picker = new FileSavePicker
        {
            SuggestedStartLocation = PickerLocationId.PicturesLibrary,
            SuggestedFileName = suggestedName
        };
        picker.FileTypeChoices.Add(displayName, new List<string> { extension });

        WinRT.Interop.InitializeWithWindow.Initialize(picker, App.WindowHandle);
        return await picker.PickSaveFileAsync();
    }
}
