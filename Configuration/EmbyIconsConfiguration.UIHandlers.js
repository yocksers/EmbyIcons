define(['configurationpage?name=EmbyIconsConfigurationUtils'], function (utils) {
    'use strict';

    function onFormChange(instance, event) {
        triggerConfigSave(instance);
        triggerPreviewUpdate(instance);

        if (event.target.matches('[data-profile-key="CommunityScoreBackgroundOpacity"]')) {
            instance.dom.opacityValue.textContent = event.target.value + '%';
        }
        if (event.target.matches('[data-profile-key="CommunityScoreIconAlignment"]')) {
            toggleRatingAppearanceControls(instance);
        }
        if (event.target.matches('[data-profile-key="UseSeriesLiteMode"]')) {
            toggleDependentSetting(instance, 'UseSeriesLiteMode', 'ShowSeriesIconsIfAllEpisodesHaveLanguage', 'This setting is ignored when Lite Mode is enabled, as Lite Mode only scans one episode.');
        }
        if (event.target.matches('[data-profile-key="UseCollectionLiteMode"]')) {
            toggleDependentSetting(instance, 'UseCollectionLiteMode', 'ShowCollectionIconsIfAllChildrenHaveLanguage', 'This setting is ignored when Lite Mode is enabled, as Lite Mode only scans one item.');
        }
    }

    function onPriorityChange(instance, event) {
        const changedSelect = event.target;
        const cornerGroup = changedSelect.closest('[data-corner-group]');
        if (!cornerGroup) return;

        const groupName = cornerGroup.getAttribute('data-corner-group');
        updatePriorityOptionsForGroup(instance, groupName);
    }

    function updatePriorityOptionsForGroup(instance, groupName) {
        const groupSelects = instance.dom.alignmentGrid.querySelectorAll(`[data-corner-group="${groupName}"] [data-profile-key$="Priority"]`);
        const selectedPriorities = new Set();

        groupSelects.forEach(select => {
            if (select.value !== '0') {
                selectedPriorities.add(select.value);
            }
        });

        groupSelects.forEach(select => {
            const ownValue = select.value;
            select.querySelectorAll('option').forEach(option => {
                if (option.value !== '0' && option.value !== ownValue) {
                    option.disabled = selectedPriorities.has(option.value);
                } else {
                    option.disabled = false;
                }
            });
        });
    }

    function updateAllPriorityGroups(instance) {
        const groupNames = new Set();
        instance.dom.prioritySelects.forEach(select => {
            const cornerGroup = select.closest('[data-corner-group]');
            if (cornerGroup) groupNames.add(cornerGroup.getAttribute('data-corner-group'));
        });
        groupNames.forEach(name => updatePriorityOptionsForGroup(instance, name));
    }

    function toggleDependentSetting(instance, controllerKey, dependentKey, dependentMessage) {
        const controllerCheckbox = instance.dom.view.querySelector(`[data-profile-key="${controllerKey}"]`);
        const dependentCheckbox = instance.dom.view.querySelector(`[data-profile-key="${dependentKey}"]`);
        if (controllerCheckbox && dependentCheckbox) {
            const container = dependentCheckbox.closest('.checkboxContainer');
            const isDisabled = controllerCheckbox.checked;
            dependentCheckbox.disabled = isDisabled;
            if (container) {
                container.style.opacity = isDisabled ? '0.6' : '1';
                container.style.pointerEvents = isDisabled ? 'none' : 'auto';
                container.title = isDisabled ? dependentMessage : '';
            }
        }
    }

    function toggleRatingAppearanceControls(instance) {
        if (!instance.dom.ratingAppearanceControls) return;

        const ratingAlignmentSelect = instance.dom.view.querySelector('[data-profile-key="CommunityScoreIconAlignment"]');
        if (ratingAlignmentSelect) {
            instance.dom.ratingAppearanceControls.style.display = ratingAlignmentSelect.value === 'Disabled' ? 'none' : 'block';
        }
    }

    function onTabChange(instance, e) {
        const currentTarget = e.currentTarget;
        instance.dom.view.querySelector('.localnav .ui-btn-active')?.classList.remove('ui-btn-active');
        currentTarget.classList.add('ui-btn-active');
        const targetId = currentTarget.getAttribute('data-target');
        instance.dom.pages.forEach(page => {
            page.classList.toggle('hide', page.id !== targetId);
        });
    }

    function triggerConfigSave(instance) {
        clearTimeout(instance.configSaveTimer);
        instance.configSaveTimer = setTimeout(() => {
            if (instance.dom.view) {
                instance.saveCurrentProfileSettings();
            }
        }, 400);
    }

    function triggerPreviewUpdate(instance) {
        clearTimeout(instance.previewUpdateTimer);
        instance.previewUpdateTimer = setTimeout(() => {
            if (instance.dom.view) updatePreview(instance);
        }, 300);
    }

    function updatePreview(instance) {
        const currentSettings = instance.getCurrentProfileSettingsFromForm();
        if (!currentSettings) return;

        instance.dom.previewImage.src = ApiClient.getUrl(instance.apiRoutes.Preview, {
            OptionsJson: JSON.stringify(currentSettings),
            v: new Date().getTime()
        });
    }

    function selectIconsFolder(instance) {
        require(['directorybrowser'], (directorybrowser) => {
            const browser = new directorybrowser();
            browser.show({
                header: 'Select Icons Folder',
                path: instance.dom.txtIconsFolder.value,
                callback: (path) => {
                    if (path) {
                        instance.dom.txtIconsFolder.value = path;
                        onFormChange(instance, { target: instance.dom.txtIconsFolder });
                        instance.validateIconsFolder();
                    }
                    browser.close();
                }
            });
        });
    }

    return {
        onFormChange: onFormChange,
        onPriorityChange: onPriorityChange,
        updateAllPriorityGroups: updateAllPriorityGroups,
        toggleRatingAppearanceControls: toggleRatingAppearanceControls,
        onTabChange: onTabChange,
        triggerPreviewUpdate: triggerPreviewUpdate,
        selectIconsFolder: selectIconsFolder
    };
});
