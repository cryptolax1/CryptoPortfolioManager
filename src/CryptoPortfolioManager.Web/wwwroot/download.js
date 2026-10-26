// Triggers a browser download for a byte array (used by the CSV export).
window.cpmDownloadFile = (fileName, contentType, bytes) => {
    const url = URL.createObjectURL(new Blob([bytes], { type: contentType }));
    const link = document.createElement("a");
    link.href = url;
    link.download = fileName;
    document.body.appendChild(link);
    link.click();
    link.remove();
    URL.revokeObjectURL(url);
};
