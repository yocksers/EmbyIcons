define(['loading', 'toast'], function (loading, toast) {
    'use strict';

    async function fetchApiRoutes(instance) {
        if (instance.apiRoutes) return;
        instance.apiRoutes = await ApiClient.ajax({ type: "GET", url: ApiClient.getUrl("EmbyIcons/ApiRoutes"), dataType: "json" });
    }

    async function refreshMemoryUsage(instance) {
        if (!instance.apiRoutes) await fetchApiRoutes(instance);
        if (!instance.dom.memoryUsageReport) return;
        try {
            instance.dom.memoryUsageReport.textContent = 'Loading...';
            const stats = await ApiClient.ajax({ type: 'GET', url: ApiClient.getUrl(instance.apiRoutes.MemoryUsage), dataType: 'json' });
            const fmt = (v) => (v / 1024 / 1024).toFixed(2) + ' MB';
            const lines = [];
            lines.push('Working Set: ' + fmt(stats.ProcessWorkingSetBytes));
            lines.push('Private Bytes: ' + fmt(stats.ProcessPrivateBytes));
            lines.push('Managed Heap: ' + fmt(stats.ManagedHeapBytes));
            if (stats.IconCacheEstimatedBytes && stats.IconCacheEstimatedBytes > 0) lines.push('Icon Cache (est): ' + fmt(stats.IconCacheEstimatedBytes));
            lines.push('As of: ' + (stats.TimestampUtc || ''));
            instance.dom.memoryUsageReport.innerHTML = lines.map(l => '<div>' + l + '</div>').join('');
        } catch (err) {
            console.error('Failed to fetch memory usage', err);
            instance.dom.memoryUsageReport.textContent = 'Error fetching memory usage.';
        }
    }

    async function validateIconsFolder(instance) {
        const path = instance.dom.txtIconsFolder.value;
        if (!path) {
            if (instance.dom.folderWarningIcon) instance.dom.folderWarningIcon.style.display = 'none';
            return;
        }

        try {
            const result = await ApiClient.ajax({ type: 'GET', url: ApiClient.getUrl(instance.apiRoutes.ValidatePath, { Path: path }), dataType: 'json' });
            if (!result.Exists) {
                instance.dom.folderWarningIcon.style.display = 'block';
                instance.dom.folderWarningIcon.title = 'The specified folder does not exist on the server.';
            } else if (!result.HasImages) {
                instance.dom.folderWarningIcon.style.display = 'block';
                instance.dom.folderWarningIcon.title = 'The specified folder exists, but no supported image files were found inside.';
            } else {
                instance.dom.folderWarningIcon.style.display = 'none';
            }
        } catch (err) {
            console.error('Error validating path', err);
            if (instance.dom.folderWarningIcon) instance.dom.folderWarningIcon.style.display = 'none';
        }
    }

    async function clearCache(instance) {
        loading.show();
        try {
            await ApiClient.ajax({ type: "POST", url: ApiClient.getUrl(instance.apiRoutes.RefreshCache) });
            toast('Cache cleared, icons will be redrawn.');
        } catch (error) {
            console.error('Error clearing EmbyIcons cache', error);
            toast({ type: 'error', text: 'Error clearing icon cache.' });
        } finally {
            loading.hide();
        }
    }

    async function calculateAspectRatio(instance) {
        const width = parseInt(instance.dom.txtAspectRatioWidth.value, 10);
        const height = parseInt(instance.dom.txtAspectRatioHeight.value, 10);

        if (!width || !height || width <= 0 || height <= 0) {
            toast({ type: 'error', text: 'Please enter valid width and height values.' });
            instance.dom.aspectRatioResultContainer.style.display = 'none';
            return;
        }

        try {
            const result = await ApiClient.ajax({ type: "GET", url: ApiClient.getUrl(instance.apiRoutes.AspectRatio, { Width: width, Height: height }), dataType: "json" });
            instance.dom.aspectDecimalValue.textContent = result.DecimalRatio.toFixed(4);
            instance.dom.aspectSnappedIconName.textContent = result.SnappedName;
            instance.dom.aspectPreciseIconName.textContent = result.PreciseName;
            instance.dom.aspectRatioResultContainer.style.display = 'block';
        } catch (err) {
            toast({ type: 'error', text: 'Error calculating aspect ratio.' });
            instance.dom.aspectRatioResultContainer.style.display = 'none';
        }
    }

    return {
        fetchApiRoutes,
        refreshMemoryUsage,
        validateIconsFolder,
        clearCache,
        calculateAspectRatio
    };
});
