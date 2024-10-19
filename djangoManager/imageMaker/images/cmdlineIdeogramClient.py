import subprocess
import argparse
import json
import os
import time

def generate_image(prompt, api_key, output_dir, num_images=1, width=1024, height=1024, timeout=300):
    command = [
        "IdeogramClient.exe",
        "--api-key", api_key,
        "--prompt", prompt,
        "--num-images", str(num_images),
        "--width", str(width),
        "--height", str(height),
        "--output-dir", output_dir
    ]

    try:
        # Start the process
        process = subprocess.Popen(command, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True)
        
        start_time = time.time()
        while True:
            # Check if the process has finished
            return_code = process.poll()
            if return_code is not None:
                break
            
            # Check if we've exceeded the timeout
            if time.time() - start_time > timeout:
                process.kill()
                raise subprocess.TimeoutExpired(command, timeout)
            
            # Wait a bit before checking again
            time.sleep(1)
        
        stdout, stderr = process.communicate()
        
        if return_code != 0:
            raise subprocess.CalledProcessError(return_code, command, stdout, stderr)
        
        # Parse the JSON output
        output = json.loads(stdout)
        
        print(f"Generated {len(output['images'])} image(s):")
        for i, image_path in enumerate(output['images'], 1):
            print(f"  {i}. {image_path}")
        
    except subprocess.TimeoutExpired:
        print(f"Error: Process timed out after {timeout} seconds")
    except subprocess.CalledProcessError as e:
        print(f"Error: {e}")
        print(f"Error output: {e.stderr}")
    except json.JSONDecodeError:
        print(f"Error: Unable to parse JSON output from IdeogramClient.exe")
        print(f"Raw output: {stdout}")

def main():
    parser = argparse.ArgumentParser(description="Generate images using Ideogram API")
    parser.add_argument("prompt", help="Text prompt for image generation")
    parser.add_argument("--api-key", required=True, help="Ideogram API key")
    parser.add_argument("--output-dir", default="./output", help="Directory to save generated images")
    parser.add_argument("--num-images", type=int, default=1, help="Number of images to generate")
    parser.add_argument("--width", type=int, default=1024, help="Image width")
    parser.add_argument("--height", type=int, default=1024, help="Image height")
    parser.add_argument("--timeout", type=int, default=300, help="Timeout in seconds")

    args = parser.parse_args()

    # Ensure output directory exists
    os.makedirs(args.output_dir, exist_ok=True)

    generate_image(args.prompt, args.api_key, args.output_dir, args.num_images, args.width, args.height, args.timeout)

if __name__ == "__main__":
    main()
