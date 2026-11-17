// Starts a browser download of bytes from the Unity heap. Used by WebDownload.cs.
mergeInto(LibraryManager.library, {
    FbxExporter_WebDownload: function (fileNamePtr, dataPtr, length, mimePtr) {
        var fileName = UTF8ToString(fileNamePtr);
        var mime = UTF8ToString(mimePtr) || "application/octet-stream";
        // Copy out of the heap: it can move or be reused once we return.
        var bytes = HEAPU8.slice(dataPtr, dataPtr + length);
        var blob = new Blob([bytes], { type: mime });
        var url = URL.createObjectURL(blob);
        var link = document.createElement("a");
        link.href = url;
        link.download = fileName;
        link.style.display = "none";
        document.body.appendChild(link);
        link.click();
        document.body.removeChild(link);
        // Give the browser time to start reading the blob before releasing it.
        setTimeout(function () { URL.revokeObjectURL(url); }, 60000);
    }
});
