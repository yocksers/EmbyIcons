define([
    'baseView',
    'loading',
    'dialogHelper',
    'toast',
    'emby-input',
    'emby-button',
    'emby-checkbox',
    'emby-select',
    'configurationpage?name=EmbyIconsConfigurationUtils',
    'configurationpage?name=EmbyIconsConfigurationDom',
    'configurationpage?name=EmbyIconsConfigurationDomCache',
    'configurationpage?name=EmbyIconsConfigurationEvents',
    'configurationpage?name=EmbyIconsConfigurationDataLoader',
    'configurationpage?name=EmbyIconsConfigurationUIHandlers',
    'configurationpage?name=EmbyIconsConfigurationProfile',
    'configurationpage?name=EmbyIconsConfigurationProfileUI',
    'configurationpage?name=EmbyIconsConfigurationScans',
    'configurationpage?name=EmbyIconsConfigurationApi'
], function (
    BaseView,
    loading,
    dialogHelper,
    toast,
    embyInput,
    embyButton,
    embyCheckbox,
    embySelect,
    utils,
    domHelpers,
    domCache,
    eventsModule,
    dataLoader,
    uiHandlers,
    profileModule,
    profileUI,
    scansModule,
    apiModule
) {
    'use strict';

    const pluginId = "b8d0f5a4-3e96-4c0f-a6e2-9f0c2ecb5c5f";

    class EmbyIconsConfigurationView extends BaseView {
        constructor(view, params) {
            super(view, params);

            this.pluginConfiguration = {};
            this.allLibraries = [];
            this.currentProfileId = null;
            this.previewUpdateTimer = null;
            this.configSaveTimer = null;
            this.folderValidationTimer = null;
            this.selectedSeriesId = null;
            this.libraryMap = new Map();
            this.profileMap = new Map();
            this.apiRoutes = null;
            this.progressPollInterval = null;

            this.dom = null;
        }

        async onResume(options) {
            super.onResume(options);
            loading.show();
            try {
                await dataLoader.loadPagePartials();
                this.dom = domCache.getDomElements(this.view);
                eventsModule.bindEvents(this);

                await this.fetchApiRoutes();
                await dataLoader.loadData(this);
            } catch (error) {
                console.error('Failed to initialize EmbyIcons configuration page', error);
                toast({ type: 'error', text: 'Error loading page. Please refresh.' });
            } finally {
                loading.hide();
            }
        }

        onPause() {
            super.onPause();
            
            if (this.previewUpdateTimer) {
                clearTimeout(this.previewUpdateTimer);
                this.previewUpdateTimer = null;
            }
            if (this.configSaveTimer) {
                clearTimeout(this.configSaveTimer);
                this.configSaveTimer = null;
            }
            if (this.folderValidationTimer) {
                clearTimeout(this.folderValidationTimer);
                this.folderValidationTimer = null;
            }
            if (this.progressPollInterval) {
                clearInterval(this.progressPollInterval);
                this.progressPollInterval = null;
            }
        }

        async fetchApiRoutes() {
            return apiModule.fetchApiRoutes(this);
        }

        async validateIconsFolder() {
            return apiModule.validateIconsFolder(this);
        }

        async clearCache() {
            return apiModule.clearCache(this);
        }

        async refreshMemoryUsage() {
            return apiModule.refreshMemoryUsage(this);
        }

        async calculateAspectRatio() {
            return apiModule.calculateAspectRatio(this);
        }

        async saveData() {
            return dataLoader.saveData(this);
        }

        onFormChange(event) {
            return uiHandlers.onFormChange(this, event);
        }

        onPriorityChange(event) {
            return uiHandlers.onPriorityChange(this, event);
        }

        updateAllPriorityGroups() {
            return uiHandlers.updateAllPriorityGroups(this);
        }

        onTabChange(e) {
            return uiHandlers.onTabChange(this, e);
        }

        selectIconsFolder() {
            return uiHandlers.selectIconsFolder(this);
        }

        populateProfileSelector() {
            return profileUI.populateProfileSelector(this);
        }

        onProfileSelected(e) {
            return profileUI.onProfileSelected(this, e);
        }

        loadProfileSettings(profileId) {
            return profileUI.loadProfileSettings(this, profileId);
        }

        getCurrentProfileSettingsFromForm() {
            return profileModule.getCurrentProfileSettingsFromForm(this);
        }

        saveCurrentProfileSettings() {
            return profileModule.saveCurrentProfileSettings(this);
        }

        addProfile() {
            return profileModule.addProfile(this);
        }

        renameProfile() {
            return profileModule.renameProfile(this);
        }

        deleteProfile() {
            return profileModule.deleteProfile(this);
        }

        async exportCurrentProfile() {
            return profileModule.exportCurrentProfile(this);
        }

        async exportAllProfiles() {
            return profileModule.exportAllProfiles(this);
        }

        async importProfiles() {
            return profileModule.importProfiles(this);
        }

        loadFilenameMappings(profile) {
            return profileModule.loadFilenameMappings(this, profile);
        }

        saveFilenameMappings(profile) {
            return profileModule.saveFilenameMappings(this, profile);
        }

        addFilenameMappingRow(mapping) {
            const keyword = mapping ? mapping.Keyword : '';
            const iconName = mapping ? mapping.IconName : '';
            const newRow = domHelpers.createFilenameMappingRow(keyword, iconName);
            this.dom.filenameMappingsContainer.appendChild(newRow);
        }

        onFilenameMappingButtonClick(e) {
            const deleteButton = e.target.closest('.btnDeleteFilenameMapping');
            if (deleteButton) {
                deleteButton.closest('.filenameMappingRow').remove();
                uiHandlers.onFormChange(this, { target: deleteButton });
            }
        }

        async runIconScan() {
            return scansModule.runIconScan(this);
        }

        renderIconManagerReport(report) {
            return scansModule.renderIconManagerReport(this, report);
        }

        async searchForSeries() {
            return scansModule.searchForSeries(this);
        }

        onSeriesSearchResultClick(e) {
            return scansModule.onSeriesSearchResultClick(this, e);
        }

        getTroubleshooterChecks() {
            return Array.from(this.dom.troubleshooterChecks)
                .filter(cb => cb.checked)
                .map(cb => cb.getAttribute('data-check-name'))
                .join(',');
        }

        async runSeriesScan() {
            return scansModule.runSeriesScan(this);
        }

        async runFullSeriesScan() {
            return scansModule.runFullSeriesScan(this);
        }

        renderSeriesReport(reports) {
            return scansModule.renderSeriesReport(this, reports);
        }

        onSeriesReportHeaderClick(e) {
            const header = e.target.closest('.collapsible-header');
            if (!header) return;

            const content = header.nextElementSibling;
            const indicator = header.querySelector('.collapsible-indicator');

            if (content && content.classList.contains('collapsible-content')) {
                const isVisible = content.style.display !== 'none';
                content.style.display = isVisible ? 'none' : 'block';

                if (indicator) {
                    indicator.style.transform = isVisible ? '' : 'rotate(-180deg)';
                }
            }
        }

        pollScanProgress(scanType, button, container) {
            return scansModule.pollScanProgress(this, scanType, button, container);
        }

        showDialog(templateId, dialogOptions) {
            const template = this.dom.view.querySelector(templateId);
            const dlg = dialogHelper.createDialog(dialogOptions);
            dlg.innerHTML = '';
            dlg.appendChild(template.content.cloneNode(true));
            dialogHelper.open(dlg);
            return dlg;
        }

        downloadJson(jsonString, filename) {
            return profileModule.downloadJson(jsonString, filename);
        }
    }

    return EmbyIconsConfigurationView;
});
