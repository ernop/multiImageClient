from django.contrib import admin
from django.urls import path
from images import views

urlpatterns = [
    path('admin/', admin.site.urls),
    path('upload/', views.upload_json, name='upload_json'),
    path('text-input/', views.text_input, name='text_input'),
]
