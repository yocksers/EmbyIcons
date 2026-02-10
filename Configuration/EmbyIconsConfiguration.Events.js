define(['configurationpage?name=EmbyIconsConfigurationUtils'], function (utils) {
    'use strict';

    function bindEvents(instance) {
        const dom = instance.dom;
        const transparentPixel = utils.transparentPixel;

        if (dom.forms) {
            dom.forms.forEach(form => {
                form.addEventListener('change', instance.onFormChange.bind(instance));
                form.addEventListener('submit', (e) => {
                    e.preventDefault();
                    instance.saveData();
                    return false;
                });
            });
        }

        if (dom.opacitySlider) {
            dom.opacitySlider.addEventListener('input', instance.onFormChange.bind(instance));
        }

        if (dom.navButtons && dom.navButtons.length) {
            dom.navButtons.forEach(navButton => {
                navButton.addEventListener('click', instance.onTabChange.bind(instance));
            });
        }

        if (dom.previewImage) {
            dom.previewImage.addEventListener('error', () => {
                dom.previewImage.src = transparentPixel;
                require(['toast'], (toast) => {
                    toast({ type: 'error', text: 'Preview generation failed.' });
                });
            });
        }

        if (dom.profileSelector) dom.profileSelector.addEventListener('change', instance.onProfileSelected.bind(instance));
        if (dom.btnAddProfile) dom.btnAddProfile.addEventListener('click', instance.addProfile.bind(instance));
        if (dom.btnRenameProfile) dom.btnRenameProfile.addEventListener('click', instance.renameProfile.bind(instance));
        if (dom.btnDeleteProfile) dom.btnDeleteProfile.addEventListener('click', instance.deleteProfile.bind(instance));
        if (dom.btnExportProfile) dom.btnExportProfile.addEventListener('click', instance.exportCurrentProfile.bind(instance));
        if (dom.btnExportAllProfiles) dom.btnExportAllProfiles.addEventListener('click', instance.exportAllProfiles.bind(instance));
        if (dom.btnImportProfile) dom.btnImportProfile.addEventListener('click', instance.importProfiles.bind(instance));

        if (dom.btnSelectIconsFolder) dom.btnSelectIconsFolder.addEventListener('click', instance.selectIconsFolder.bind(instance));
        if (dom.txtIconsFolder) dom.txtIconsFolder.addEventListener('input', utils.debounce(instance.validateIconsFolder.bind(instance), 500));

        if (dom.btnClearCache) dom.btnClearCache.addEventListener('click', instance.clearCache.bind(instance));
        if (dom.btnRunIconScan) dom.btnRunIconScan.addEventListener('click', instance.runIconScan.bind(instance));
        if (dom.btnShowStatistics) dom.btnShowStatistics.addEventListener('click', instance.showStatistics.bind(instance));

        if (dom.prioritySelects && dom.prioritySelects.length) {
            dom.prioritySelects.forEach(select => {
                select.addEventListener('change', instance.onPriorityChange.bind(instance));
            });
        }

        if (dom.txtSeriesSearch) dom.txtSeriesSearch.addEventListener('input', utils.debounce(instance.searchForSeries.bind(instance), 300));
        if (dom.seriesSearchResults) dom.seriesSearchResults.addEventListener('click', instance.onSeriesSearchResultClick.bind(instance));
        if (dom.btnRunSeriesScan) dom.btnRunSeriesScan.addEventListener('click', instance.runSeriesScan.bind(instance));
        if (dom.btnRunFullSeriesScan) dom.btnRunFullSeriesScan.addEventListener('click', instance.runFullSeriesScan.bind(instance));
        if (dom.seriesReportContainer) dom.seriesReportContainer.addEventListener('click', instance.onSeriesReportHeaderClick.bind(instance));

        if (dom.btnRefreshMemoryUsage) dom.btnRefreshMemoryUsage.addEventListener('click', instance.refreshMemoryUsage.bind(instance));

        if (dom.btnCalculateAspectRatio) dom.btnCalculateAspectRatio.addEventListener('click', instance.calculateAspectRatio.bind(instance));

        if (dom.btnAddFilenameMapping) dom.btnAddFilenameMapping.addEventListener('click', instance.addFilenameMappingRow.bind(instance, null));
        if (dom.filenameMappingsContainer) dom.filenameMappingsContainer.addEventListener('click', instance.onFilenameMappingButtonClick.bind(instance));

        instance.documentClickHandler = (e) => {
            if (dom.seriesSearchResults && dom.txtSeriesSearch && !dom.seriesSearchResults.contains(e.target) && !dom.txtSeriesSearch.contains(e.target)) {
                dom.seriesSearchResults.style.display = 'none';
            }
        };
        document.addEventListener('click', instance.documentClickHandler);

        utils.initializeCollapsibleSections(instance.view);
    }

    return {
        bindEvents: bindEvents
    };
});
