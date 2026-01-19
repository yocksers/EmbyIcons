define(['configurationpage?name=EmbyIconsConfigurationProfile'], function (profileModule) {
    'use strict';

    function populateProfileSelector(instance) {
        const select = instance.dom.profileSelector;
        if (!select) {
            console.warn('Profile selector element not found in DOM');
            return;
        }
        select.innerHTML = instance.pluginConfiguration.Profiles.map(p => `<option value="${p.Id}">${p.Name}</option>`).join('');
        instance.currentProfileId = select.value;
        if (select.embyselect) select.embyselect.refresh();
    }

    function onProfileSelected(instance, e) {
        loadProfileSettings(instance, e.target.value);
    }

    function loadProfileSettings(instance, profileId) {
        if (!instance.dom || !instance.dom.allProfileInputs) {
            console.warn('DOM elements not ready for loadProfileSettings');
            return;
        }
        instance.currentProfileId = profileId;
        const profile = instance.pluginConfiguration.Profiles.find(p => p.Id === profileId);
        if (!profile) return;

        renderProfileSettings(instance, profile.Settings);
        populateLibraryAssignments(instance, profileId);
        profileModule.loadFilenameMappings(instance, profile);
        require(['configurationpage?name=EmbyIconsConfigurationUIHandlers'], (uiHandlers) => {
            uiHandlers.triggerPreviewUpdate(instance);
            uiHandlers.updateAllPriorityGroups(instance);
        });
    }

    function renderProfileSettings(instance, settings) {
        instance.dom.allProfileInputs.forEach(el => {
            const key = el.getAttribute('data-profile-key');
            const value = settings[key];
            if (el.type === 'checkbox') {
                el.checked = value;
            } else {
                el.value = value ?? '';
            }
        });
        instance.dom.allProfileSelects.forEach(s => {
            if (s.embyselect) s.embyselect.refresh();
        });

        if (instance.dom.opacitySlider && instance.dom.opacityValue) {
            instance.dom.opacityValue.textContent = instance.dom.opacitySlider.value + '%';
        }

        require(['configurationpage?name=EmbyIconsConfigurationUIHandlers'], (uiHandlers) => {
            uiHandlers.toggleRatingAppearanceControls(instance);
        });
    }

    async function populateLibraryAssignments(instance, profileId) {
        const container = instance.dom.librarySelectionContainer;
        if (!instance.allLibraries) {
            const virtualFolders = await ApiClient.getVirtualFolders();
            const ignoredLibraryTypes = ['music', 'collections', 'playlists', 'boxsets'];
            instance.allLibraries = virtualFolders.Items.filter(lib => !lib.CollectionType || !ignoredLibraryTypes.includes(lib.CollectionType.toLowerCase()));
        }

        const libraryToProfileMap = new Map(instance.pluginConfiguration.LibraryProfileMappings.map(m => [m.LibraryId, m.ProfileId]));

        let html = '';
        for (const library of instance.allLibraries) {
            const assignedProfileId = libraryToProfileMap.get(library.Id);
            const isAssignedToCurrent = assignedProfileId === profileId;
            const isAssignedToOther = assignedProfileId && !isAssignedToCurrent;

            const isChecked = isAssignedToCurrent;
            const isDisabled = isAssignedToOther;

            let title = '';
            if (isDisabled) {
                const otherProfileName = instance.profileMap.get(assignedProfileId) || 'another profile';
                title = ` title="This library is managed by the '${otherProfileName}' profile."`;
            }

            html += `<div class="checkboxContainer"${title}>
                            <label>
                                <input is="emby-checkbox" type="checkbox" data-library-id="${library.Id}" ${isChecked ? 'checked' : ''} ${isDisabled ? 'disabled' : ''} />
                                <span>${library.Name}</span>
                            </label>
                         </div>`;
        }
        container.innerHTML = html;
    }

    return {
        populateProfileSelector: populateProfileSelector,
        onProfileSelected: onProfileSelected,
        loadProfileSettings: loadProfileSettings,
        renderProfileSettings: renderProfileSettings
    };
});
