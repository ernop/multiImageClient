from django.shortcuts import render, redirect, get_object_or_404
from django.contrib import messages
from django.http import HttpRequest, HttpResponse, JsonResponse
from typing import Any
from .models import Prompt, ImageGeneration, ImageProducerType, ImageProducerVersion, ImageSave, ImageSaveType
from .forms import ImageGenerationForm
from ideogram.ideogramClient import IdeogramService
from django.conf import settings
import os
import aiohttp
import asyncio
from asgiref.sync import sync_to_async, async_to_sync
import requests
import json
import time
from django.views.decorators.http import require_http_methods
from django.views.decorators.csrf import csrf_protect
from django.utils.decorators import method_decorator
from django.core.cache import cache

@sync_to_async
def create_or_get_prompt(text, user):
    return Prompt.objects.get_or_create(Text=text, Creator=user)

@sync_to_async
def get_producer_version(generator):
    return get_object_or_404(ImageProducerVersion, ImageProducerType__Name=generator)

@sync_to_async
def create_image_generation(user, prompt, producer_version, details, uri, result):
    return ImageGeneration.objects.create(
        User=user,
        Prompt=prompt,
        ImageProducerVersion=producer_version,
        Details=details,
        URI=uri,
        Result=result
    )

@sync_to_async
def create_image_save(image_generation, image_save_type, file_path):
    return ImageSave.objects.create(
        ImageGeneration=image_generation,
        ImageSaveType=image_save_type,
        FilePath=file_path
    )

@csrf_protect
@require_http_methods(["POST"])
async def generate_image_async(request: HttpRequest) -> JsonResponse:
    import ipdb; ipdb.set_trace()
    try:
        data = json.loads(request.body)
        form = ImageGenerationForm(data)
        if form.is_valid():
            prompt = form.cleaned_data['prompt']
            generator = form.cleaned_data['generator']

            # Get user ID asynchronously
            user_id = await sync_to_async(lambda: request.user.id)()

            # Create a task ID
            task_id = f"{user_id}_{int(time.time())}"

            # Start the image generation process asynchronously
            asyncio.create_task(process_image_generation(user_id, prompt, generator, task_id))

            return JsonResponse({"status": "processing", "task_id": task_id})
        else:
            return JsonResponse({"status": "error", "errors": form.errors}, status=400)
    except json.JSONDecodeError:
        return JsonResponse({"status": "error", "message": "Invalid JSON"}, status=400)
    except Exception as e:
        return JsonResponse({"status": "error", "message": str(e)}, status=500)

async def process_image_generation(user_id, prompt, generator, task_id):
    # Implementation of the image generation process
    # You'll need to use sync_to_async for database operations here as well
    user = await sync_to_async(lambda: User.objects.get(id=user_id))()
    prompt_obj, _ = await create_or_get_prompt(prompt, user)
    producer_version = await get_producer_version(generator)

    ideogram_service = IdeogramService(settings.IDEOGRAM_API_KEY, max_concurrency=1)
    prompt_details = {"prompt": prompt, "ideogram_details": {}}

    result = await ideogram_service.process_prompt_async(prompt_details, {"ideogram_request_count": 0})

    if result["is_success"]:
        image_generation = await create_image_generation(
            user,
            prompt_obj,
            producer_version,
            result.get("prompt_details", {}),
            result["url"],
            result
        )

        image_path = await download_and_save_image(result["url"], image_generation.id)

        await create_image_save(
            image_generation,
            await sync_to_async(ImageSaveType.objects.get)(Name=ImageSaveType.ImageSaveTypeChoices.RAW),
            image_path
        )

        # Store the result somewhere (e.g., cache or database) associated with the task_id
        # For simplicity, let's assume we have a function to do this
        await store_task_result(task_id, {"status": "success", "image_id": image_generation.id})
    else:
        await store_task_result(task_id, {"status": "error", "message": result.get("error_message", "Unknown error")})

@require_http_methods(["GET"])
def check_image_status(request: HttpRequest) -> JsonResponse:
    task_id = request.GET.get('task_id')
    if not task_id:
        return JsonResponse({"status": "error", "message": "No task ID provided"}, status=400)

    # Retrieve the task result (implement this function based on your storage method)
    result = get_task_result(task_id)
 
    if result is None:
        return JsonResponse({"status": "processing"})
    else:
        return JsonResponse(result)

def list_images(request: HttpRequest) -> HttpResponse:
    images = ImageGeneration.objects.all().order_by('-created')
    return render(request, 'list_images.html', {'images': images})

def image_details(request: HttpRequest, image_id: int) -> HttpResponse:
    image = get_object_or_404(ImageGeneration, id=image_id)
    return render(request, 'image_details.html', {'image': image})

def generate_image(request: HttpRequest) -> HttpResponse:
    if request.method == 'POST': 
        import ipdb; ipdb.set_trace()
        form = ImageGenerationForm(request.POST)
        if form.is_valid():
            prompt = form.cleaned_data['prompt']
            generator = form.cleaned_data['generator']

            prompt_obj, _ = Prompt.objects.get_or_create(Text=prompt, Creator=request.user)
            producer_version = ImageProducerVersion.objects.filter(ImageProducerType__Name=generator).first()

            ideogram_service = IdeogramService(settings.IDEOGRAM_API_KEY, max_concurrency=1)
            prompt_details = {"prompt": prompt, "ideogram_details": {}}

            result = ideogram_service.process_prompt(prompt_details, {"ideogram_request_count": 0})

            if result["is_success"]:
                image_generation = ImageGeneration.objects.create(
                    User=request.user,
                    Prompt=prompt_obj,
                    ImageProducerVersion=producer_version,
                    Details=result.get("prompt_details", {}),
                    URI=result["url"],
                    Result=result
                )

                image_path = download_and_save_image(result["url"], image_generation.id)

                ImageSave.objects.create(
                    ImageGeneration=image_generation,
                    ImageSaveType=ImageSaveType.objects.get(Name=ImageSaveType.ImageSaveTypeChoices.RAW),
                    FilePath=image_path
                )

                messages.success(request, 'Image generated successfully.')
                return redirect('image_details', image_id=image_generation.id)
            else:
                messages.error(request, f'Image generation failed: {result.get("error_message", "Unknown error")}')
        else:
            messages.error(request, 'Invalid form submission.')
    else:
        form = ImageGenerationForm()

    return render(request, 'generate_image.html', {'form': form})

def download_and_save_image(url: str, image_id: int) -> str:
    response = requests.get(url)
    if response.status_code == 200:
        file_name = f"generated_image_{image_id}.png"
        file_path = os.path.join(settings.MEDIA_ROOT, 'generated_images', file_name)
        os.makedirs(os.path.dirname(file_path), exist_ok=True)
        with open(file_path, 'wb') as f:
            f.write(response.content)
        return file_path
    else:
        raise Exception(f"Failed to download image: HTTP {response.status_code}")

def store_task_result(task_id: str, result: dict) -> None:
    cache.set(f"task_result_{task_id}", result, timeout=3600)  # Stores the result for 1 hour

def get_task_result(task_id: str) -> dict:
    return cache.get(f"task_result_{task_id}")
