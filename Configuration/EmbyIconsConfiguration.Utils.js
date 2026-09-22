define([], function () {
    'use strict';

    const transparentPixel = 'data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNkYAAAAAYAAjCB0C8AAAAASUVORK5CYII=';

    function debounce(func, wait) {
        let timeout;
        return function () {
            const context = this;
            const args = Array.prototype.slice.call(arguments);
            clearTimeout(timeout);
            timeout = setTimeout(function () {
                func.apply(context, args);
            }, wait);
        };
    }

    function initializeCollapsibleSections(view) {
        const headers = view.querySelectorAll('.collapsible-header');
        headers.forEach(header => {
            const section = header.parentElement;
            const content = header.nextElementSibling;
            const icon = header.querySelector('.collapse-icon');
            
            if (content && content.classList.contains('collapsible-content')) {
                const shouldBeOpen = section.hasAttribute('data-default-open');
                
                if (shouldBeOpen) {
                    content.style.display = 'block';
                    if (icon) {
                        icon.style.transform = 'rotate(180deg)';
                    }
                } else {
                    content.style.display = 'none';
                    if (icon) {
                        icon.style.transform = 'rotate(0deg)';
                    }
                }
            }
            
            header.addEventListener('click', function() {
                if (content && content.classList.contains('collapsible-content')) {
                    const isHidden = content.style.display === 'none';
                    content.style.display = isHidden ? 'block' : 'none';
                    
                    if (icon) {
                        icon.style.transform = isHidden ? 'rotate(180deg)' : 'rotate(0deg)';
                    }
                }
            });
        });
    }

    function expandAllSections(view) {
        const contents = view.querySelectorAll('.collapsible-content');
        const icons = view.querySelectorAll('.collapse-icon');
        
        contents.forEach(content => {
            content.style.display = 'block';
        });
        
        icons.forEach(icon => {
            icon.style.transform = 'rotate(180deg)';
        });
    }

    function collapseAllSections(view) {
        const contents = view.querySelectorAll('.collapsible-content');
        const icons = view.querySelectorAll('.collapse-icon');
        
        contents.forEach(content => {
            content.style.display = 'none';
        });
        
        icons.forEach(icon => {
            icon.style.transform = 'rotate(0deg)';
        });
    }

    function applyAdvancedMode(view, enabled) {
        view.querySelectorAll('[data-advanced-only]').forEach(el => {
            el.style.display = enabled ? (el.getAttribute('data-advanced-only-display') || '') : 'none';
        });
    }

    function initializeSegmentedControls(view) {
        const controls = view.querySelectorAll('.segmented-control');
        controls.forEach(control => {
            const buttons = Array.from(control.querySelectorAll('.segment-button'));
            const panels = buttons
                .map(btn => view.querySelector('#' + btn.getAttribute('data-segment')))
                .filter(Boolean);

            function activate(targetId) {
                buttons.forEach(btn => {
                    btn.classList.toggle('button-submit', btn.getAttribute('data-segment') === targetId);
                });
                panels.forEach(panel => {
                    panel.style.display = (panel.id === targetId) ? '' : 'none';
                });
            }

            buttons.forEach(btn => {
                btn.addEventListener('click', () => activate(btn.getAttribute('data-segment')));
            });

            const initiallyActive = control.querySelector('.segment-button.button-submit') || buttons[0];
            if (initiallyActive) activate(initiallyActive.getAttribute('data-segment'));
        });
    }

    return {
        debounce: debounce,
        transparentPixel: transparentPixel,
        initializeCollapsibleSections: initializeCollapsibleSections,
        expandAllSections: expandAllSections,
        collapseAllSections: collapseAllSections,
        applyAdvancedMode: applyAdvancedMode,
        initializeSegmentedControls: initializeSegmentedControls
    };
});
