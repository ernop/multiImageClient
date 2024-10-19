document.addEventListener('DOMContentLoaded', () => {
    const viewer = document.getElementById('fullscreen-viewer');
    const fullscreenImage = document.getElementById('fullscreen-image');
    const prevButton = document.getElementById('prev-button');
    const nextButton = document.getElementById('next-button');

    let images = [];
    let currentIndex = 0;

    function loadImageList() {
        fetch(`/get-image-list?dir=${encodeURIComponent(imageDir)}`)
            .then(response => response.json())
            .then(data => {
                images = data.images;
                if (images.length > 0) {
                    showImage(0);
                }
            })
            .catch(error => console.error('Error loading image list:', error));
    }

    function showImage(index) {
        const imageName = images[index];
        fullscreenImage.src = `/get-image?dir=${encodeURIComponent(imageDir)}&name=${encodeURIComponent(imageName)}`;
        currentIndex = index;
    }

    function openViewer() {
        viewer.style.display = 'flex';
    }

    function closeViewer() {
        viewer.style.display = 'none';
    }

    prevButton.addEventListener('click', () => {
        currentIndex = (currentIndex - 1 + images.length) % images.length;
        showImage(currentIndex);
    });

    nextButton.addEventListener('click', () => {
        currentIndex = (currentIndex + 1) % images.length;
        showImage(currentIndex);
    });

    document.addEventListener('keydown', (e) => {
        if (viewer.style.display === 'flex') {
            if (e.key === 'ArrowLeft') {
                prevButton.click();
            } else if (e.key === 'ArrowRight') {
                nextButton.click();
            } else if (e.key === 'Escape') {
                closeViewer();
            }
        }
    });

    loadImageList();
    openViewer();
});