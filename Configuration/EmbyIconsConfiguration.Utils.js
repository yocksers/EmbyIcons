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
            header.addEventListener('click', function() {
                const content = this.nextElementSibling;
                const icon = this.querySelector('.collapse-icon');
                
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

    return {
        debounce: debounce,
        transparentPixel: transparentPixel,
        initializeCollapsibleSections: initializeCollapsibleSections
    };
});
