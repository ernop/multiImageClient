from django.contrib import admin
from django.urls import path
from images import views
from images import image_views, image_viewer
from django.views.decorators.csrf import csrf_exempt

urlpatterns = [
    path('admin/', admin.site.urls),
    path('', views.index, name='index'),
    path('upload/', views.upload_json, name='upload_json'), 
    
    path('text-input/', views.text_input, name='text_input'),
    path('images/', image_views.list_images, name='list_images'),
    path('images/<int:image_id>/', image_views.image_details, name='image_details'),
    path('prompt-search/', views.prompt_search, name='prompt-search'),
    
    path('view-image-dir/', image_viewer.view_image_dir, name='view_image_dir'),
    path('get-image-list/', image_viewer.get_image_list, name='get_image_list'),
    path('get-image/', image_viewer.get_image, name='get_image'),
]
