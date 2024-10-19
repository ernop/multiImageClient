import os
from django.conf import settings
from django.shortcuts import render
from django.http import JsonResponse, HttpResponse


from django.shortcuts import render, redirect
from django.contrib import messages
from django.core.files.storage import default_storage
from django.core.files.base import ContentFile
import json
from .models import *
from typing import Any, List

from django.db.models import Q
from .models import Prompt


def view_image_dir(request: Any) -> Any:
    image_dir="/mnt/d/proj/multiImageClient/MultiImageClient/saves/2024-10-15-Tuesday"
    return render(request, 'view_image_dir.html', {'image_dir': image_dir})

def get_image_list(request: Any) -> JsonResponse:
    image_dir = request.GET.get('dir')
    if not image_dir or not os.path.exists(image_dir):
        return JsonResponse({'error': 'Invalid directory'}, status=400)
    
    images = [f for f in os.listdir(image_dir) if f.lower().endswith(('.png', '.jpg', '.jpeg', '.gif'))]
    return JsonResponse({'images': images})

def get_image(request: Any) -> HttpResponse:
    image_dir = request.GET.get('dir')
    image_name = request.GET.get('name')
    if not image_dir or not image_name:
        return HttpResponse('Invalid request', status=400)
    
    image_path = os.path.join(image_dir, image_name)
    if not os.path.exists(image_path):
        return HttpResponse('Image not found', status=404)
    
    with open(image_path, 'rb') as img_file:
        return HttpResponse(img_file.read(), content_type='image/png')  # Adjust content_type if needed