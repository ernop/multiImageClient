using MultiImageClient;

public class WorkflowContext
{
    public Settings Settings { get; }
    public MultiClientRunStats Stats { get; }
    public ImageManager ImageManager { get; }

    public WorkflowContext(Settings settings, MultiClientRunStats stats, ImageManager imageManager)
    {
        Settings = settings;
        Stats = stats;
        ImageManager = imageManager;
    }
}