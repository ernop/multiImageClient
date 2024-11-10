from django.db import models
from django.contrib.auth.models import User
from django.utils import timezone
from typing import Optional, Dict, Any

#Rules:
# never remove this rules comment
# we don't use i18n now, thanks
# we use uppercase for field names
# don't abbreviate items in an enum class.

class BaseModel(models.Model):
    created = models.DateTimeField(default=timezone.now)
    updated = models.DateTimeField(auto_now=True)

    def clink(self, text: Optional[str] = None, wrap: bool = True, skip_btn: bool = False, klasses: Optional[list[str]] = None, tooltip: Optional[str] = None) -> str:
        if skip_btn:
            klass = ""
        else:
            klass = "btn btn-default"
        if klasses:
            klass += " ".join(klasses)
        if wrap:
            wrap = ""
        else:
            wrap = " nb"
        if not text:
            text = self
        if not tooltip:
            tooltip = ""

        return '<a class="%s%s" title="%s" href="../../%s/%s/?id=%d">%s</a>' % (klass, wrap, tooltip, 'images', self.__class__.__name__.lower(), self.id, text)

    class Meta:
        app_label = 'images'
        abstract = True

class Prompt(BaseModel):
    id = models.AutoField(primary_key=True)
    Text = models.TextField()
    Creator = models.ForeignKey(User, on_delete=models.CASCADE)
    CreationType = models.ForeignKey('CreationType', on_delete=models.CASCADE, null=True)
    CreationTypeData = models.JSONField(default=dict, null=True, blank=True)
    #for every creationType there will be a format for the creation type data which we can deserialize.

    def __str__(self) -> str:
        return self.Text


class CreationType(BaseModel):
    class CreationTypeChoices(models.TextChoices):
        USER_TYPED = 'User Typed', 'User Typed'
        IMAGE_COMBINATION = 'Image Combination', 'Image Combination'
        TEXT_COMBINATION = 'Text Combination', 'Text Combination'
        TEXT_REWRITE = 'Text Rewrite', 'Text Rewrite'
        LOADED_FROM_FILE = "Loaded From File", "Loaded From File"

    id = models.AutoField(primary_key=True)
    Name = models.CharField(
        max_length=20,
        choices=CreationTypeChoices.choices,
        default=CreationTypeChoices.USER_TYPED
    )

    def __str__(self):
        return self.Name


class Rewriter(BaseModel):
    class RewriterChoices(models.TextChoices):
        CLAUDE = 'Claude', 'Claude'
        LLAMA = 'Llama', 'Llama'

    id = models.AutoField(primary_key=True)
    Name = models.CharField(
        max_length=20,
        choices=RewriterChoices.choices,
        default=RewriterChoices.CLAUDE
    )

class PromptTag(BaseModel):
    id = models.AutoField(primary_key=True)
    Prompt = models.ForeignKey(Prompt, related_name="prompttags", on_delete=models.CASCADE)
    Tag = models.ForeignKey('Tag', related_name="prompttags", on_delete=models.CASCADE)

    def __str__(self):
        return f"{self.Tag}"

class Tag(BaseModel):
    id = models.AutoField(primary_key=True)
    Name = models.CharField(max_length=255)
    Hidden = models.BooleanField(default=False)
    HideContainingPrompts = models.BooleanField(default=False)

    def __str__(self):
        return self.Name    

#user generated an image
class ImageGeneration(BaseModel):
    id = models.BigAutoField(primary_key=True)
    User = models.ForeignKey(User, on_delete=models.CASCADE)
    Prompt = models.ForeignKey(Prompt, related_name="image_generations", on_delete=models.CASCADE)
    
    ImageProducerVersion = models.ForeignKey('ImageProducerVersion', on_delete=models.CASCADE)
    Details = models.JSONField(default=dict, blank=True) 
    #this will be deserialized based on the imageProducerVersion
    #this is details of the generation parameters etc. like resolution 

    URI = models.CharField(max_length=1024)
    Result = models.JSONField(default=dict, blank=True)

    def __str__(self):
        return f"Gen {self.Prompt.Text[:20]} by {self.User.username} on {self.ImageProducerVersion.ImageProducerType.Name} {self.ImageProducerVersion.Version}"

#okay so the way requests from all image producers except BFL are async is annoying
# whereas BFL just has two steps - 1. upload the request (sync) then an endpoing 2. to check if its done yet.
# so this class holds the request before it can be finally saved into an ImageGeneration
class BFLRequest(BaseModel):
    id = models.AutoField(primary_key=True)
    Prompt = models.ForeignKey(Prompt, on_delete=models.CASCADE)
    Status = models.BooleanField(default=False)
    User = models.ForeignKey(User, on_delete=models.CASCADE)

# raw or the various types of annotations for saved images.
class ImageSaveType(BaseModel):
    class ImageSaveTypeChoices(models.TextChoices):
        RAW = 'Raw', 'Raw'
        FULL_ANNOTATION = 'Full Annotation', 'Full Annotation'
        INITIAL_IDEA = 'Initial Idea', 'Initial Idea'
        FINAL_PROMPT = 'Final Prompt', 'Final Prompt'

    id = models.AutoField(primary_key=True)
    Name = models.CharField(
        max_length=20,
        choices=ImageSaveTypeChoices.choices,
        default=ImageSaveTypeChoices.RAW
    )

    def __str__(self):
        return self.Name

# we saved a version of an image 
class ImageSave(BaseModel):
    ImageGeneration = models.ForeignKey(ImageGeneration, related_name='image_saves', on_delete=models.CASCADE)
    ImageSaveType = models.ForeignKey(ImageSaveType, on_delete=models.CASCADE)
    FilePath = models.CharField(max_length=1024)

class ImageProducerType(BaseModel):
    class ImageProducerTypeChoices(models.TextChoices): 
        MIDJOURNEY = 'Midjourney', 'Midjourney'
        DE3 = 'De3', 'De3'
        BFL = 'BFL', 'BFL'
        IDEOGRAM = 'Ideogram', 'Ideogram'

    id = models.AutoField(primary_key=True)
    Name = models.CharField(
        max_length=20,
        choices=ImageProducerTypeChoices.choices,
        default=ImageProducerTypeChoices.BFL
    )

    def __str__(self):
        return self.Name

#details of a specific image producer
class ImageProducerTraits(BaseModel):
    id = models.AutoField(primary_key=True)
    ImageProducerType = models.ForeignKey('ImageProducerType', on_delete=models.CASCADE)
    SaveExtension = models.CharField(max_length=255)

    def __str__(self):
        return f"{self.ImageProducerType} {self.SaveExtension}"

#an api, e.g.
#A version label of a producer, like ideogram2?
class ImageProducerVersion(BaseModel):
    id = models.AutoField(primary_key=True)
    ImageProducerType = models.ForeignKey(ImageProducerType, on_delete=models.CASCADE)
    Version = models.CharField(max_length=255)

    def __str__(self):
        return f"{self.ImageProducerType} {self.Version}"
