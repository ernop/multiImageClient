import os
import sys
import shutil
from datetime import datetime
from PIL import Image

def stretch_image(img, target_size):
    """Stretch image to fit target_size, minimizing distortion"""
    aspect_ratio = img.width / img.height
    target_ratio = target_size[0] / target_size[1]

    if aspect_ratio > target_ratio:
        new_height = int(img.height * (target_size[0] / img.width))
        img = img.resize((target_size[0], new_height), Image.LANCZOS)
        img = img.resize(target_size, Image.LANCZOS)
    else:
        new_width = int(img.width * (target_size[1] / img.height))
        img = img.resize((new_width, target_size[1]), Image.LANCZOS)
        img = img.resize(target_size, Image.LANCZOS)

    return img

def generate_unique_filename():
    """Generate a unique filename based on current timestamp"""
    timestamp = datetime.now().strftime("%Y%m%d_%H%M%S")
    return f"combined_a4_{timestamp}.png"

def create_directory(directory):
    """Create a directory if it doesn't exist"""
    if not os.path.exists(directory):
        os.makedirs(directory)

def concat_images_a4(image_paths):
    # Create necessary directories
    ready_to_print_dir = "ready_to_print"
    combined_dir = os.path.join(ready_to_print_dir, "combined")
    create_directory(ready_to_print_dir)
    create_directory(combined_dir)

    # A4 size in pixels at 300 DPI
    a4_width, a4_height = 2480, 3508
    quadrant_size = (a4_width // 2, a4_height // 2)

    new_image = Image.new('RGBA', (a4_width, a4_height), (0, 0, 0, 0))

    for i, path in enumerate(image_paths):
        with Image.open(path) as img:
            img = img.convert('RGBA')
            img = stretch_image(img, quadrant_size)
            x = (i % 2) * quadrant_size[0]
            y = (i // 2) * quadrant_size[1]
            new_image.paste(img, (x, y), img)

    # Generate unique filename and save
    output_filename = generate_unique_filename()
    output_path = os.path.join(ready_to_print_dir, output_filename)
    new_image.save(output_path, 'PNG', dpi=(300, 300))
    print(f"Images combined and saved as '{output_path}'")

    # Move source images to the 'combined' subfolder
    for path in image_paths:
        filename = os.path.basename(path)
        new_path = os.path.join(combined_dir, filename)
        shutil.move(path, new_path)
    print(f"Source images moved to '{combined_dir}'")

if __name__ == "__main__":
    import ipdb;ipdb.set_trace()
    if len(sys.argv) != 5:
        print("Usage: python script.py image1.jpg image2.jpg image3.jpg image4.jpg")
        sys.exit(1)

    image_paths = sys.argv[1:]
    concat_images_a4(image_paths)
