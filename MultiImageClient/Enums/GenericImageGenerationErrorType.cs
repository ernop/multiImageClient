namespace MultiImageClient.Enums
{
    public enum GenericImageGenerationErrorType
    {
        RequestModerated = 1,
        ContentModerated = 2,
        NoMoneyLeft = 3,
        Unknown = 4, //refine this if it happens; this is just a catch-all.
        NoImagesGenerated = 5,
    }


}
