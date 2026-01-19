define([], function () {
    'use strict';

    function getDomElements(view) {
        return {
            view: view,
            forms: view.querySelectorAll('.embyIconsForm'),
            allConfigInputs: view.querySelectorAll('[data-config-key]'),
            allProfileInputs: view.querySelectorAll('[data-profile-key]'),
            allProfileSelects: view.querySelectorAll('select[is="emby-select"][data-profile-key]'),
            navButtons: view.querySelectorAll('.localnav .nav-button'),
            profileSelector: view.querySelector('#selActiveProfile'),
            btnAddProfile: view.querySelector('#btnAddProfile'),
            btnRenameProfile: view.querySelector('#btnRenameProfile'),
            btnDeleteProfile: view.querySelector('#btnDeleteProfile'),
            btnExportProfile: view.querySelector('#btnExportProfile'),
            btnExportAllProfiles: view.querySelector('#btnExportAllProfiles'),
            btnImportProfile: view.querySelector('#btnImportProfile'),
            btnSelectIconsFolder: view.querySelector('#btnSelectIconsFolder'),
            txtIconsFolder: view.querySelector('#txtIconsFolder'),
            folderWarningIcon: view.querySelector('#folderWarningIcon'),
            btnClearCache: view.querySelector('#btnClearCache'),
            btnRunIconScan: view.querySelector('#btnRunIconScan'),
            btnShowStatistics: view.querySelector('#btnShowStatistics'),
            iconManagerHowTo: view.querySelector('#iconManagerHowTo'),
            iconManagerStatsContainer: view.querySelector('#iconManagerStatsContainer'),
            librarySelectionContainer: view.querySelector('#librarySelectionContainer'),
            opacitySlider: view.querySelector('[data-profile-key="CommunityScoreBackgroundOpacity"]'),
            opacityValue: view.querySelector('.valCommunityScoreBackgroundOpacity'),
            ratingAppearanceControls: view.querySelector('#ratingAppearanceControls'),
            pages: view.querySelectorAll('#settingsPage, #advancedPage, #readmePage, #iconManagerPage, #troubleshooterPage'),
            previewImage: view.querySelector('.previewImage'),
            iconManagerReportContainer: view.querySelector('#iconManagerReportContainer'),
            alignmentGrid: view.querySelector('.alignment-grid'),
            prioritySelects: view.querySelectorAll('[data-profile-key$="Priority"]'),
            txtSeriesSearch: view.querySelector('#txtSeriesSearch'),
            seriesSearchResults: view.querySelector('#seriesSearchResults'),
            btnRunSeriesScan: view.querySelector('#btnRunSeriesScan'),
            btnRunFullSeriesScan: view.querySelector('#btnRunFullSeriesScan'),
            seriesReportContainer: view.querySelector('#seriesReportContainer'),
            troubleshooterChecks: view.querySelectorAll('#troubleshooterChecksContainer input[type=checkbox]'),
            btnRefreshMemoryUsage: view.querySelector('#btnRefreshMemoryUsage'),
            memoryUsageReport: view.querySelector('#memoryUsageReport'),
            btnCalculateAspectRatio: view.querySelector('#btnCalculateAspectRatio'),
            txtAspectRatioWidth: view.querySelector('#txtAspectRatioWidth'),
            txtAspectRatioHeight: view.querySelector('#txtAspectRatioHeight'),
            aspectRatioResultContainer: view.querySelector('#aspectRatioResultContainer'),
            aspectDecimalValue: view.querySelector('#aspectDecimalValue'),
            aspectSnappedIconName: view.querySelector('#aspectSnappedIconName'),
            aspectPreciseIconName: view.querySelector('#aspectPreciseIconName'),
            filenameMappingsContainer: view.querySelector('#filenameMappingsContainer'),
            btnAddFilenameMapping: view.querySelector('#btnAddFilenameMapping'),
        };
    }

    return {
        getDomElements: getDomElements
    };
});
