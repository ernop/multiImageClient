from django import forms
from .models import Prompt, ImageProducerType

class ImageGenerationForm(forms.Form):
    prompt = forms.CharField(widget=forms.Textarea)
    existing_prompt = forms.ModelChoiceField(
        queryset=Prompt.objects.none(),  # Start with an empty queryset
        required=False,
        empty_label="Choose an existing prompt (optional)"
    )
    generator = forms.ChoiceField(choices=ImageProducerType.ImageProducerTypeChoices.choices)

    def __init__(self, *args, **kwargs):
        super().__init__(*args, **kwargs)
        self.fields['existing_prompt'].widget.attrs.update({
            'class': 'select2',
            'data-ajax--url': '/prompt-search/',  # Update this URL as needed
            'data-ajax--cache': 'true',
            'data-ajax--delay': '25',
            'data-ajax--data-type': 'json',
            'data-minimum-input-length': '2',
        })

    def clean(self):
        cleaned_data = super().clean()
        prompt = cleaned_data.get('prompt')
        existing_prompt = cleaned_data.get('existing_prompt')

        if not prompt and not existing_prompt:
            raise forms.ValidationError("Please enter a prompt or choose an existing one.")
        
        if existing_prompt:
            cleaned_data['prompt'] = existing_prompt.Text

        return cleaned_data
