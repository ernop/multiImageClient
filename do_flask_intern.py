from flask import Flask, request, jsonify
import torch
from transformers import AutoModel, AutoTokenizer
from PIL import Image
import base64
from io import BytesIO
import torchvision.transforms as T
from torchvision.transforms.functional import InterpolationMode

app = Flask(__name__)

print("Loading InternVL3-1B-Pretrained base model... This will take a minute on first run")
model_name = "OpenGVLab/InternVL3-1B-Pretrained"

# Load model and tokenizer with GPU support
model = AutoModel.from_pretrained(
    model_name,
    torch_dtype=torch.bfloat16,
    device_map="auto",
    trust_remote_code=True
)
tokenizer = AutoTokenizer.from_pretrained(model_name, trust_remote_code=True)

print(f"Model loaded! Using device: {model.device}")
print(f"GPU Memory allocated: {torch.cuda.memory_allocated() / 1024**3:.2f} GB")

# Image preprocessing functions from InternVL3 documentation
IMAGENET_MEAN = (0.485, 0.456, 0.406)
IMAGENET_STD = (0.229, 0.224, 0.225)

def build_transform(input_size):
    MEAN, STD = IMAGENET_MEAN, IMAGENET_STD
    transform = T.Compose([
        T.Lambda(lambda img: img.convert('RGB') if img.mode != 'RGB' else img),
        T.Resize((input_size, input_size), interpolation=InterpolationMode.BICUBIC),
        T.ToTensor(),
        T.Normalize(mean=MEAN, std=STD)
    ])
    return transform

def find_closest_aspect_ratio(aspect_ratio, target_ratios, width, height, image_size):
    best_ratio_diff = float('inf')
    best_ratio = (1, 1)
    area = width * height
    for ratio in target_ratios:
        target_aspect_ratio = ratio[0] / ratio[1]
        ratio_diff = abs(aspect_ratio - target_aspect_ratio)
        if ratio_diff < best_ratio_diff:
            best_ratio_diff = ratio_diff
            best_ratio = ratio
        elif ratio_diff == best_ratio_diff:
            if area > 0.5 * image_size * image_size * ratio[0] * ratio[1]:
                best_ratio = ratio
    return best_ratio

def dynamic_preprocess(image, min_num=1, max_num=12, image_size=448, use_thumbnail=False):
    orig_width, orig_height = image.size
    aspect_ratio = orig_width / orig_height

    target_ratios = set(
        (i, j) for n in range(min_num, max_num + 1) for i in range(1, n + 1) for j in range(1, n + 1) if
        i * j <= max_num and i * j >= min_num)
    target_ratios = sorted(target_ratios, key=lambda x: x[0] * x[1])

    target_aspect_ratio = find_closest_aspect_ratio(
        aspect_ratio, target_ratios, orig_width, orig_height, image_size)

    target_width = image_size * target_aspect_ratio[0]
    target_height = image_size * target_aspect_ratio[1]
    blocks = target_aspect_ratio[0] * target_aspect_ratio[1]

    resized_img = image.resize((target_width, target_height))
    processed_images = []
    for i in range(blocks):
        box = (
            (i % (target_width // image_size)) * image_size,
            (i // (target_width // image_size)) * image_size,
            ((i % (target_width // image_size)) + 1) * image_size,
            ((i // (target_width // image_size)) + 1) * image_size
        )
        split_img = resized_img.crop(box)
        processed_images.append(split_img)
    assert len(processed_images) == blocks
    if use_thumbnail and len(processed_images) != 1:
        thumbnail_img = image.resize((image_size, image_size))
        processed_images.append(thumbnail_img)
    return processed_images

def process_image(image, input_size=448, max_num=12):
    transform = build_transform(input_size=input_size)
    images = dynamic_preprocess(image, image_size=input_size, use_thumbnail=True, max_num=max_num)
    pixel_values = [transform(img) for img in images]
    pixel_values = torch.stack(pixel_values)
    return pixel_values

@app.route('/generate', methods=['POST'])
def generate():
    """
    Expects JSON:
    {
        "image": "data:image/png;base64,..." or raw base64 string,
        "prompt": "Describe this image",
        "max_tokens": 512,
        "temperature": 0.8,
        "top_p": 0.9,
        "top_k": 50,
        "repetition_penalty": 1.1,
        "do_sample": true
    }
    Note: The <image> token will be automatically prepended to your prompt.
    Higher temperature (0.7-1.5) = more creative/varied responses
    Lower temperature (0.1-0.5) = more deterministic/focused responses
    """
    try:
        print("Received request to /generate")
        data = request.json
        print(f"Request data keys: {data.keys() if data else 'No data'}")
        
        image_input = data.get('image', '')
        prompt = data.get('prompt', 'Describe this image in detail.')
        max_tokens = data.get('max_tokens', 512)
        temperature = data.get('temperature', 0.8)
        top_p = data.get('top_p', 0.9)
        top_k = data.get('top_k', 50)
        repetition_penalty = data.get('repetition_penalty', 1.1)
        do_sample = data.get('do_sample', True)
        
        print(f"Prompt: {prompt[:50]}...")
        print(f"Generation config - max_tokens: {max_tokens}, temperature: {temperature}, top_p: {top_p}, top_k: {top_k}, do_sample: {do_sample}, repetition_penalty: {repetition_penalty}")
        print(f"Image data length: {len(image_input) if image_input else 0}")
        
        if not image_input:
            return jsonify({'error': 'No image provided'}), 400
        
        print("Decoding base64 image...")
    except Exception as e:
        error_msg = f"Error parsing request: {str(e)}"
        print(error_msg)
        import traceback
        traceback.print_exc()
        return jsonify({'error': error_msg}), 500
    
    try:
        # Handle base64 images
        if image_input.startswith('data:image'):
            # Extract base64 data after comma
            print("Decoding data URI...")
            base64_data = image_input.split(',')[1]
            image_bytes = base64.b64decode(base64_data)
            image = Image.open(BytesIO(image_bytes)).convert('RGB')
        elif image_input.startswith(('http://', 'https://')):
            # URL - not implemented for security, but you could add requests here
            return jsonify({'error': 'URL images not supported yet'}), 400
        else:
            # Assume raw base64
            print("Decoding raw base64...")
            image_bytes = base64.b64decode(image_input)
            image = Image.open(BytesIO(image_bytes)).convert('RGB')
        
        print(f"Image loaded: {image.size}, mode: {image.mode}")
        
        # Process image into pixel_values tensor
        print("Processing image to pixel_values...")
        pixel_values = process_image(image, input_size=448, max_num=12)
        print(f"Pixel values shape: {pixel_values.shape}")
        
        pixel_values = pixel_values.to(torch.bfloat16).cuda()
        print("Moved to GPU")
        
        # Prepare the prompt in InternVL format (must include <image> token)
        question = f'<image>\n{prompt}'
        
        generation_config = dict(
            max_new_tokens=max_tokens,
            do_sample=do_sample,
            temperature=temperature if do_sample else 1.0,
            top_p=top_p if do_sample else 1.0,
            top_k=top_k if do_sample else 50,
            repetition_penalty=repetition_penalty,
        )
        
        print("Calling model.chat()...")
        # Generate response using InternVL's chat method
        # Correct signature: model.chat(tokenizer, pixel_values, question, generation_config, ...)
        response = model.chat(
            tokenizer,
            pixel_values,
            question,
            generation_config
        )
        
        print(f"Got response: {response[:100]}..." if len(response) > 100 else f"Got response: {response}")
        
        return jsonify({
            'response': response
        })
        
    except Exception as e:
        error_msg = f"Error in generate endpoint: {str(e)}"
        print(error_msg)
        import traceback
        traceback.print_exc()
        return jsonify({'error': str(e)}), 500

@app.route('/health', methods=['GET'])
def health():
    return jsonify({
        'status': 'ok',
        'model': model_name,
        'device': str(model.device),
        'gpu_memory_gb': torch.cuda.memory_allocated() / 1024**3
    })

if __name__ == '__main__':
    print("\n" + "="*60)
    print("InternVL3-1B-Pretrained Server")
    print("="*60)
    print(f"Server running on http://localhost:11415")
    print(f"Health check: http://localhost:11415/health")
    print("\nExample request (with varied responses):")
    print("""
    POST http://localhost:11415/generate
    {
        "image": "data:image/png;base64,iVBORw0KG...",
        "prompt": "What is in this image?",
        "max_tokens": 512,
        "temperature": 0.8,
        "top_p": 0.9,
        "top_k": 50,
        "repetition_penalty": 1.1,
        "do_sample": true
    }
    
    For more deterministic responses, use:
    "temperature": 0.1, "do_sample": false
    """)
    print("="*60 + "\n")
    app.run(host='0.0.0.0', port=11415, debug=False)