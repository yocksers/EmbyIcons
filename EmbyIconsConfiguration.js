define(['baseView', 'loading', 'emby-input', 'emby-button', 'emby-checkbox', 'emby-select'], function (BaseView, loading) {
    'use strict';

    const pluginId = "b8d0f5a4-3e96-4c0f-a6e2-9f0c2ecb5c5f";

    function View(view, params) {
        BaseView.apply(this, arguments);

        this.previewUpdateTimer = null;

        view.querySelector('.nav-settings').addEventListener('click', () => {
            view.querySelector('#settingsPage').classList.remove('hide');
            view.querySelector('#readmePage').classList.add('hide');
            view.querySelector('.nav-settings').classList.add('ui-btn-active');
            view.querySelector('.nav-readme').classList.remove('ui-btn-active');
        });

        view.querySelector('.nav-readme').addEventListener('click', () => {
            view.querySelector('#settingsPage').classList.add('hide');
            view.querySelector('#readmePage').classList.remove('hide');
            view.querySelector('.nav-settings').classList.remove('ui-btn-active');
            view.querySelector('.nav-readme').classList.add('ui-btn-active');
        });

        view.querySelector('.embyIconsForm').addEventListener('submit', (e) => {
            e.preventDefault();
            this.saveData();
            return false;
        });

        view.querySelector('.embyIconsForm').addEventListener('change', () => {
            if (this.previewUpdateTimer) {
                clearTimeout(this.previewUpdateTimer);
            }
            this.previewUpdateTimer = setTimeout(() => this.updatePreview(), 400);
        });

        view.querySelector('#btnSelectIconsFolder').addEventListener('click', this.selectIconsFolder.bind(this));
        view.querySelector('#btnClearCache').addEventListener('click', this.clearCache.bind(this));

        const opacitySlider = view.querySelector('#rngCommunityScoreBackgroundOpacity');
        const opacityValue = view.querySelector('#valCommunityScoreBackgroundOpacity');
        if (opacitySlider && opacityValue) {
            opacitySlider.addEventListener('input', (e) => {
                opacityValue.textContent = e.target.value + '%';
            });
        }
    }

    Object.assign(View.prototype, BaseView.prototype);

    View.prototype.clearCache = function () {
        loading.show();
        ApiClient.ajax({
            type: "POST",
            url: ApiClient.getUrl("EmbyIcons/RefreshCache")
        }).then(() => {
            loading.hide();
            require(['toast'], function (toast) {
                toast('Icon cache cleared successfully. Posters will update as you browse.');
            });
        }).catch(() => {
            loading.hide();
            require(['toast'], function (toast) {
                toast({
                    type: 'error',
                    text: 'Error clearing icon cache.'
                });
            });
        });
    };

    View.prototype.selectIconsFolder = function () {
        var instance = this;

        require(['directorybrowser'], function (directorybrowser) {

            var picker = new directorybrowser(instance.view);

            picker.show({
                header: 'Select Icons Folder',
                path: instance.view.querySelector('#txtIconsFolder').value,
                callback: function (path) {
                    if (path) {
                        const txtIconsFolder = instance.view.querySelector('#txtIconsFolder');
                        txtIconsFolder.value = path;
                        txtIconsFolder.dispatchEvent(new Event('change', {
                            bubbles: true,
                            cancelable: true
                        }));
                    }
                    picker.close();
                }
            });
        });
    };

    View.prototype.onResume = function (options) {
        BaseView.prototype.onResume.apply(this, arguments);
        this.loadData();
    };

    View.prototype.loadData = function () {
        loading.show();
        const view = this.view;
        const self = this;

        Promise.all([
            ApiClient.getPluginConfiguration(pluginId),
            ApiClient.getVirtualFolders()
        ]).then(function ([config, virtualFolders]) {

            view.querySelectorAll('[data-config-key]').forEach(el => {
                const key = el.getAttribute('data-config-key');
                const value = config[key];

                if (el.type === 'checkbox') {
                    el.checked = value;
                } else {
                    el.value = value == null ? '' : value;
                }
            });

            const opacitySlider = view.querySelector('#rngCommunityScoreBackgroundOpacity');
            const opacityValue = view.querySelector('#valCommunityScoreBackgroundOpacity');
            if (opacitySlider && opacityValue) {
                opacitySlider.value = config.CommunityScoreBackgroundOpacity == null ? 80 : config.CommunityScoreBackgroundOpacity;
                opacityValue.textContent = opacitySlider.value + '%';
            }

            const ignoredLibraryTypes = ['music', 'collections'];
            const filteredLibraries = virtualFolders.Items.filter(lib => {
                if (!lib.CollectionType) {
                    return true;
                }
                return !ignoredLibraryTypes.includes(lib.CollectionType.toLowerCase());
            });

            self.populateLibraries(filteredLibraries, config.SelectedLibraries);

            self.updatePreview();
            loading.hide();
        });
    };

    View.prototype.populateLibraries = function (libraries, selectedLibrariesCsv) {
        const view = this.view;
        const container = view.querySelector('#librarySelectionContainer');

        let html = '';
        libraries.forEach(library => {
            const checkboxId = 'chk_lib_' + library.Id;
            html += `<div class="checkboxContainer"><label><input is="emby-checkbox" type="checkbox" id="${checkboxId}" data-library-name="${library.Name}" /><span>${library.Name}</span></label></div>`;
        });
        container.innerHTML = html;

        const selectedNames = (selectedLibrariesCsv || '').split(',').map(s => s.trim().toLowerCase()).filter(Boolean);
        const selectedNamesSet = new Set(selectedNames);

        container.querySelectorAll('input[type=checkbox]').forEach(checkbox => {
            const libName = checkbox.getAttribute('data-library-name').toLowerCase();
            if (selectedNamesSet.has(libName)) {
                checkbox.checked = true;
            }
        });

        container.addEventListener('change', (e) => {
            if (e.target.type === 'checkbox') {
                const selectedNames = [];
                container.querySelectorAll('input[type=checkbox]:checked').forEach(chk => {
                    selectedNames.push(chk.getAttribute('data-library-name'));
                });
                const hiddenInput = view.querySelector('#txtSelectedLibraries');
                hiddenInput.value = selectedNames.join(',');
                hiddenInput.dispatchEvent(new Event('change', { bubbles: true, cancelable: true }));
            }
        });
    };

    View.prototype.getFormOptions = function () {
        const view = this.view;
        const options = {};

        view.querySelectorAll('[data-config-key]').forEach(el => {
            const key = el.getAttribute('data-config-key');
            const type = el.getAttribute('type');

            if (type === 'checkbox') {
                options[key] = el.checked;
            } else if (type === 'number' || el.classList.contains('slider')) {
                options[key] = parseInt(el.value) || 0;
            } else {
                options[key] = el.value;
            }
        });

        return options;
    };

    View.prototype.saveData = function () {
        loading.show();
        const instance = this;

        ApiClient.getPluginConfiguration(pluginId).then((config) => {

            const formOptions = instance.getFormOptions();
            for (const key in formOptions) {
                config[key] = formOptions[key];
            }

            ApiClient.updatePluginConfiguration(pluginId, config).then((result) => {
                Dashboard.processPluginConfigurationUpdateResult(result);
                loading.hide();
                require(['toast'], function (toast) {
                    toast('EmbyIcons settings saved.');
                });
            }).catch(() => {
                loading.hide();
                require(['toast'], function (toast) {
                    toast({
                        type: 'error',
                        text: 'Error saving EmbyIcons settings.'
                    });
                });
            });
        });
    };

    View.prototype.updatePreview = function () {
        const view = this.view;
        const options = this.getFormOptions();

        const params = {
            OptionsJson: JSON.stringify(options),
            v: new Date().getTime()
        };

        view.querySelector('.previewImage').src = ApiClient.getUrl('EmbyIcons/Preview', params);
    };

    return View;
});