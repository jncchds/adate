// Small helpers the play page calls: follow the conversation as it grows, and start new words at the top.
window.adate = {
    scrollToTop: (element) => {
        if (element) {
            element.scrollTop = 0;
        }
    },
    scrollToEnd: (element) => {
        if (element) {
            requestAnimationFrame(() => { element.scrollTop = element.scrollHeight; });
        }
    },
};
