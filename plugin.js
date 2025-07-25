﻿(function () {
    const pluginId = "b8d0f5a4-3e96-4c0f-a6e2-9f0c2ecb5c5f";

    const pluginVersion = "5.00.0";

    window.Dashboard.getPluginPages = function () {
        return [
            {
                name: 'EmbyIcons',
                path: Dashboard.getConfigurationPageUrl('EmbyIconsConfiguration'),
                plugin: 'EmbyIcons',
                icon: 'photo'
            }
        ];
    };

    window.Dashboard.getPluginRoutes = function () {
        return [
            {
                path: '/plugins/embyiconsconfiguration.html',
                id: 'embyiconsconfiguration',

                controller: 'plugins/embyicons/embyiconsconfiguration.js?v=' + pluginVersion,

                template: 'plugins/embyicons/embyiconsconfiguration.html',
                title: 'EmbyIcons',
                mobile: true
            }
        ];
    };

})();