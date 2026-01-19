define([], function () {
    'use strict';

    const pluginId = "b8d0f5a4-3e96-4c0f-a6e2-9f0c2ecb5c5f";

    async function loadPagePartials() {
        const parts = [
            { id: 'settingsPage', page: 'EmbyIconsConfigurationSettings' },
            { id: 'advancedPage', page: 'EmbyIconsConfigurationAdvanced' },
            { id: 'iconManagerPage', page: 'EmbyIconsConfigurationIconManager' },
            { id: 'troubleshooterPage', page: 'EmbyIconsConfigurationTroubleshooter' },
            { id: 'readmePage', page: 'EmbyIconsConfigurationReadme' },
            { id: 'addProfileDialogTemplate', page: 'EmbyIconsConfigurationAddProfileTemplate' },
            { id: 'renameProfileDialogTemplate', page: 'EmbyIconsConfigurationRenameProfileTemplate' }
        ];

        const promises = parts.map(p =>
            fetch('/web/configurationpage?name=' + p.page)
                .then(r => r.ok ? r.text() : '')
                .then(html => {
                    if (html) {
                        const el = document.getElementById(p.id);
                        if (el) el.innerHTML = html;
                    }
                })
                .catch(err => console.error('Failed to load page part: ' + p.page, err))
        );

        await Promise.all(promises);
    }

    async function loadData(instance) {
        try {
            const [config, virtualFolders, user] = await Promise.all([
                ApiClient.getPluginConfiguration(pluginId),
                ApiClient.getVirtualFolders(),
                ApiClient.getCurrentUser()
            ]);

            instance.pluginConfiguration = config;
            instance.currentUser = user;
            loadGlobalSettings(instance, config);

            const ignoredLibraryTypes = ['music', 'collections', 'playlists', 'boxsets'];
            instance.allLibraries = virtualFolders.Items.filter(lib => !lib.CollectionType || !ignoredLibraryTypes.includes(lib.CollectionType.toLowerCase()));
            instance.libraryMap = new Map(instance.allLibraries.map(lib => [lib.Id, lib.Name]));
            instance.profileMap = new Map(instance.pluginConfiguration.Profiles.map(p => [p.Id, p.Name]));

            instance.populateProfileSelector();
            
            if (instance.dom && instance.dom.profileSelector && instance.dom.profileSelector.value) {
                instance.loadProfileSettings(instance.dom.profileSelector.value);
            }
            
            instance.validateIconsFolder();
            instance.refreshMemoryUsage().catch(err => { /* ignore */ });
        } catch (error) {
            console.error('Failed to load EmbyIcons configuration', error);
            require(['toast'], (toast) => {
                toast({ type: 'error', text: 'Error loading configuration.' });
            });
            throw error;
        }
    }

    function loadGlobalSettings(instance, config) {
        instance.dom.allConfigInputs.forEach(el => {
            const key = el.getAttribute('data-config-key');
            const value = config[key];
            if (el.type === 'checkbox') {
                el.checked = value;
            } else {
                el.value = value ?? '';
            }
        });
    }

    async function saveData(instance) {
        if (!instance.pluginConfiguration.Profiles || !instance.pluginConfiguration.Profiles.length) {
            require(['toast'], (toast) => {
                toast({ type: 'error', text: 'Cannot save settings. You must create at least one profile.' });
            });
            return;
        }

        require(['loading'], (loading) => {
            loading.show();
        });

        clearTimeout(instance.configSaveTimer);
        instance.saveCurrentProfileSettings();

        instance.dom.allConfigInputs.forEach(el => {
            const key = el.getAttribute('data-config-key');
            if (el.type === 'checkbox') {
                instance.pluginConfiguration[key] = el.checked;
            } else {
                instance.pluginConfiguration[key] = el.value;
            }
        });

        try {
            const result = await ApiClient.updatePluginConfiguration(pluginId, instance.pluginConfiguration);
            Dashboard.processPluginConfigurationUpdateResult(result);
        } catch (error) {
            console.error('Error saving EmbyIcons settings', error);
            require(['toast'], (toast) => {
                toast({ type: 'error', text: 'Error saving settings.' });
            });
        } finally {
            require(['loading'], (loading) => {
                loading.hide();
            });
        }
    }

    return {
        loadPagePartials: loadPagePartials,
        loadData: loadData,
        saveData: saveData
    };
});
