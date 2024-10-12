from django.contrib import admin
from django.utils.html import mark_safe
from django.urls import path, reverse
from django.http import HttpResponseRedirect
from django.shortcuts import render, redirect
from django.contrib.admin import helpers
import os
import requests
from .models import (
    Prompt, CreationType, Rewriter, PromptTag, Tag, ImageGeneration,
    ImageSaveType, ImageSave, ImageProducerType, ImageProducerTraits,
    ImageProducerVersion
)

# Register your models here.

class BaseModelAdmin(admin.ModelAdmin):
    def get_fields(self, request, obj=None):
        fields = list(super().get_fields(request, obj))
        for field in ['created', 'updated']:
            if field in fields:
                fields.remove(field)
            
            fields.append(field)
        return fields

    def get_list_display(self, request):
        list_display = super().get_list_display(request)
        if not isinstance(list_display, list):
            list_display = list(list_display)
        if 'created' not in list_display:
            list_display.append('created')
        if 'updated' not in list_display:
            list_display.append('updated')
        return list_display

@admin.register(Prompt)
class PromptAdmin(BaseModelAdmin):
    list_display = ('id', 'Text', 'get_creator', 'get_creation_type', 'get_tags', 'get_image_generations')
    actions = ['produce_generation']
    
    def get_creator(self, obj):
        if obj.Creator:
            url = reverse("admin:auth_user_change", args=[obj.Creator.id])
            return mark_safe(f'<a href="{url}">{obj.Creator.username}</a>')
        return "N/A"

    get_creator.short_description = 'Creator'

    def get_creation_type(self, obj):
        return mark_safe(obj.CreationType.clink(text=obj.CreationType.Name) if obj.CreationType else "N/A")
    get_creation_type.short_description = 'Creation Type'

    def get_tags(self, obj):
        return mark_safe(", ".join([pt.Tag.clink(text=pt.Tag.Name) for pt in obj.prompttags.all()]))
    get_tags.short_description = 'Tags'

    def get_image_generations(self, obj):
        return mark_safe(", ".join([gen.clink(text=f"Gen {gen.id}") for gen in obj.image_generations.all()]))
    get_image_generations.short_description = 'Image Generations'
 
    def produce_generation(self, request, queryset):
        if 'apply' in request.POST:
            # This is the form submission
            if len(queryset) != 1:
                self.message_user(request, "Please select only one prompt for generation.")
                return

            prompt = queryset[0]
            producer_id = request.POST.get('image_producer')
            details = request.POST.get('details')
            
            # Create the ImageGeneration object
            image_producer = ImageProducerType.objects.get(id=producer_id)
            image_generation = ImageGeneration.objects.create(
                User=request.user,
                Prompt=prompt,
                ImageProducerVersion=image_producer.imageproducerversion_set.first(),
                Details=details,
                URI="placeholder_uri"  # Replace with actual generated image URI
            )

            self.message_user(request, f"Generation created for prompt: {prompt.Text[:50]}...")
            return redirect('admin:images_imagegeneration_change', image_generation.id)
        
        else:
            # This is the initial action call, display the form
            if len(queryset) != 1:
                self.message_user(request, "Please select only one prompt for generation.")
                return

            prompt = queryset[0]
            image_producers = ImageProducerType.objects.all()

            context = {
                'title': f"Generate Image for Prompt: {prompt.Text[:50]}...",
                'prompt': prompt,
                'image_producers': image_producers,
                'queryset': queryset,
                'opts': self.model._meta,
                'action_checkbox_name': helpers.ACTION_CHECKBOX_NAME,
            }
            return render(request, 'admin/prompt_generate.html', context)

    produce_generation.short_description = "Produce generation for selected prompt"

    def get_urls(self):
        urls = super().get_urls()
        custom_urls = [
            path('<int:prompt_id>/generate/', self.admin_site.admin_view(self.generate_view), name='prompt-generate'),
        ]
        return custom_urls + urls

    def generate_view(self, request, prompt_id):
        prompt = Prompt.objects.get(id=prompt_id)
        image_producers = ImageProducerType.objects.all()
        import ipdb;ipdb.set_trace()
        if request.method == 'POST':
            
            producer_id = request.POST.get('image_producer')
            details = request.POST.get('details')
            
            # Here you would typically call your image generation service
            # For now, we'll just create a placeholder ImageGeneration object
            image_producer = ImageProducerType.objects.get(id=producer_id)
            image_generation = ImageGeneration.objects.create(
                User=request.user,
                Prompt=prompt,
                ImageProducerVersion=image_producer.imageproducerversion_set.first(),  # Assuming there's at least one version
                Details=details,
                URI="placeholder_uri"  # Replace with actual generated image URI
            )

            self.message_user(request, f"Generation created for prompt: {prompt.Text[:50]}...")
            return redirect('admin:images_imagegeneration_change', image_generation.id)

        context = {
            'title': f"Generate Image for Prompt: {prompt.Text[:50]}...",
            'prompt': prompt,
            'image_producers': image_producers,
            'opts': self.model._meta,
        }
        return render(request, 'admin/prompt_generate.html', context)

@admin.register(CreationType)
class CreationTypeAdmin(BaseModelAdmin):
    list_display = ('id', 'Name')

@admin.register(Rewriter)
class RewriterAdmin(BaseModelAdmin):
    list_display = ('id', 'Name')

@admin.register(PromptTag)
class PromptTagAdmin(BaseModelAdmin):
    list_display = ('id', 'get_prompt', 'get_tag')

    def get_prompt(self, obj):
        return mark_safe(obj.Prompt.clink(text=obj.Prompt.Text[:60] + '...'))
    get_prompt.short_description = 'Prompt'

    def get_tag(self, obj):
        return mark_safe(obj.Tag.clink(text=obj.Tag.Name))
    get_tag.short_description = 'Tag'

@admin.register(Tag)
class TagAdmin(BaseModelAdmin):
    list_display = ('id', 'Name', 'Hidden', 'HideContainingPrompts', 'get_prompts')

    def get_prompts(self, obj):
        return mark_safe(", ".join([pt.Prompt.clink(text=pt.Prompt.Text[:20] + '...') for pt in obj.prompttags.all()]))
    get_prompts.short_description = 'Prompts'

@admin.register(ImageGeneration)
class ImageGenerationAdmin(BaseModelAdmin):
    list_display = ('id', 'get_user', 'get_prompt', 'get_image_producer_version', 'get_image_saves', 'display_image')
    
    def get_user(self, obj):
        if obj.User:
            url = reverse("admin:auth_user_change", args=[obj.User.id])
            return mark_safe(f'<a href="{url}">{obj.User.username}</a>')
        return "N/A"
    get_user.short_description = 'User'

    def get_prompt(self, obj):
        return mark_safe(obj.Prompt.clink(text=obj.Prompt.Text[:20] + '...'))
    get_prompt.short_description = 'Prompt'

    def get_image_producer_version(self, obj):
        return mark_safe(obj.ImageProducerVersion.clink(text=f"{obj.ImageProducerVersion.ImageProducerType.Name} {obj.ImageProducerVersion.Version}"))
    get_image_producer_version.short_description = 'Image Producer Version'

    def get_image_saves(self, obj):
        return mark_safe(", ".join([save.clink(text=f"{save.ImageSaveType.Name} {save.id}") for save in obj.image_saves.all()]))
    get_image_saves.short_description = 'Image Saves'

    def display_image(self, obj):
        if obj.URI:
            return mark_safe(f'<img src="{obj.URI}" width="100" height="100" />')
        return "No image"
    display_image.short_description = 'Image'

@admin.register(ImageSaveType)
class ImageSaveTypeAdmin(BaseModelAdmin):
    list_display = ('id', 'Name')

@admin.register(ImageSave)
class ImageSaveAdmin(BaseModelAdmin):
    list_display = ('id', 'get_image_generation', 'get_image_save_type', 'FilePath', 'display_image')
    list_filter = ('ImageSaveType',)
    actions = ['download_image']

    def get_image_generation(self, obj):
        return mark_safe(obj.ImageGeneration.clink(text=f"Gen {obj.ImageGeneration.id}"))
    get_image_generation.short_description = 'Image Generation'

    def get_image_save_type(self, obj):
        return mark_safe(obj.ImageSaveType.clink(text=obj.ImageSaveType.Name))
    get_image_save_type.short_description = 'Image Save Type'

    def display_image(self, obj):
        if os.path.exists(obj.FilePath):
            return mark_safe(f'<img src="/media/{obj.FilePath}" width="100" height="100" />')
        return "Not downloaded"
    display_image.short_description = 'Image'

    def download_image(self, request, queryset):
        for image_save in queryset:
            if not os.path.exists(image_save.FilePath):
                try:
                    response = requests.get(image_save.ImageGeneration.URI)
                    response.raise_for_status()
                    os.makedirs(os.path.dirname(image_save.FilePath), exist_ok=True)
                    with open(image_save.FilePath, 'wb') as f:
                        f.write(response.content)
                    self.message_user(request, f"Successfully downloaded image for ImageSave {image_save.id}")
                except Exception as e:
                    self.message_user(request, f"Failed to download image for ImageSave {image_save.id}: {str(e)}")
            else:
                self.message_user(request, f"Image for ImageSave {image_save.id} already exists")
        return HttpResponseRedirect(request.get_full_path())
    download_image.short_description = "Download selected images"

@admin.register(ImageProducerType)
class ImageProducerTypeAdmin(BaseModelAdmin):
    list_display = ('id', 'Name', 'get_traits')

    def get_traits(self, obj):
        traits = obj.imageproducertraits_set.first()
        if traits:
            return mark_safe(traits.clink(text=f"Ext: {traits.SaveExtension}"))
        return "No traits"
    get_traits.short_description = 'Traits'

@admin.register(ImageProducerTraits)
class ImageProducerTraitsAdmin(BaseModelAdmin):
    list_display = ('id', 'get_image_producer', 'SaveExtension')

    def get_image_producer(self, obj):
        return mark_safe(obj.ImageProducerType.clink(text=obj.ImageProducerType.Name))
    get_image_producer.short_description = 'Image Producer'

@admin.register(ImageProducerVersion)
class ImageProducerVersionAdmin(BaseModelAdmin):
    list_display = ('id', 'get_image_producer', 'Version')
    list_filter = ('ImageProducerType',)

    def get_image_producer(self, obj):
        return mark_safe(obj.ImageProducerType.clink(text=obj.ImageProducerType.Name))
    get_image_producer.short_description = 'Image Producer'

# Register other models with their respective admin classes




