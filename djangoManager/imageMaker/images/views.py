import os
from django.conf import settings
 
from django.shortcuts import render, redirect
from django.contrib import messages
from django.core.files.storage import default_storage
from django.core.files.base import ContentFile
import json
from .models import *
from typing import Any, List
from django.http import JsonResponse
from django.db.models import Q
from .models import Prompt

# Create your views here.

def index(request: Any) -> Any:
    links = [

    ]
    return render(request, 'index.html', {})



def prompt_search(request):
    query = request.GET.get('q', '')
    page = int(request.GET.get('page', 1))
    per_page = 30
    offset = (page - 1) * per_page

    prompts = Prompt.objects.filter(Q(Text__icontains=query) | Q(id__icontains=query))[offset:offset+per_page]
    total_count = Prompt.objects.filter(Q(Text__icontains=query) | Q(id__icontains=query)).count()

    results = [{'id': prompt.id, 'text': f"{prompt.id}: {prompt.Text[:100]}"} for prompt in prompts]
    return JsonResponse({
        'results': results,
        'total_count': total_count,
        'pagination': {
            'more': total_count > (page * per_page)
        }
    })

def upload_json(request):
    if request.method == 'POST' and request.FILES['json_file']:
        import ipdb;ipdb.set_trace()
        json_file = request.FILES['json_file']
        
        # Save the file temporarily
        path = default_storage.save('tmp/json_upload.json', ContentFile(json_file.read()))
        
        try:
            with default_storage.open(path) as f:
                data = json.load(f)
            
            # Process the JSON data here
            # You can access any model and perform operations
            # For example:
            # new_prompt = Prompt.objects.create(Text=data['prompt_text'], Creator=request.user)
            
            messages.success(request, 'JSON file uploaded and processed successfully.')
        except json.JSONDecodeError:
            messages.error(request, 'Invalid JSON file.')
        except Exception as e:
            messages.error(request, f'An error occurred: {str(e)}')
        finally:
            # Clean up the temporary file
            default_storage.delete(path)
        
        return redirect('upload_json')
    
    return render(request, 'upload_json.html')

def text_input(request: Any) -> Any:
    
    if request.method == 'POST':
        text = request.POST.get('text_content', '')
        if text:
            # Process the text
            processed_text: List[str] = set([el.strip() for el in process_text_input(text)])
            loadedFromFile = CreationType.objects.get(Name=CreationType.CreationTypeChoices.LOADED_FROM_FILE)
            for el in processed_text:
                prompt, created = Prompt.objects.get_or_create(Text=el, Creator=request.user, CreationType=loadedFromFile)
            messages.success(request, 'Text processed successfully.')
        else:
            messages.error(request, 'No text was provided.')
        
        return redirect('text_input')
    
    return render(request, 'text_input.html')

def process_text_input(text: str) -> List[str]:
    # Remove leading/trailing whitespace
    text = text.strip()
    
    # Check if the text is wrapped in quotes
    if text.startswith('"') and text.endswith('"'):
        # Treat as a single multiline string
        return [text[1:-1].strip()]  # Remove the quotes and strip again
    else:
        # Split by lines, strip each line, and filter out empty lines
        return [line.strip() for line in text.split('\n') if line.strip()]

